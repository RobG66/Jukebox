using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Jukebox.Mpv;

/// <summary>
/// High-level wrapper around the libmpv C API.
/// Handles: mpv handle creation, initialization, file loading, playback
/// control (play/pause/seek/stop), property get/set, and event observation.
/// </summary>
/// <remarks>
/// <para>
/// The render context (OpenGL) is managed separately by <see cref="MpvView"/>,
/// which creates the render context using the mpv handle from this class.
/// </para>
/// <para>
/// <b>Threading:</b> mpv handle is thread-safe for most API calls. Property
/// observation events are received on a background thread and marshalled to
/// the UI thread via callbacks.
/// </para>
/// </remarks>
public sealed class MpvContext : IDisposable
{
    private IntPtr _mpv;
    private IntPtr _renderContext;
    private Thread? _eventThread;
    private CancellationTokenSource? _eventCts;
    private bool _disposed;

    // Keep delegates alive — libmpv stores function pointers that must
    // remain valid for the lifetime of the render context.
    private MpvNative.MpvGetProcAddressDelegate? _getProcAddressDelegate;

    // Pre-allocated unmanaged buffers for Render() — avoids 180+
    // AllocHGlobal/FreeHGlobal pairs per second at 60fps.
    private IntPtr _renderParamsPtr;
    private IntPtr _renderFboPtr;
    private IntPtr _renderFlipYPtr;
    private int _renderParamSize;

    /// <summary>Gets the raw mpv handle. Used by MpvView to create the render context.</summary>
    internal IntPtr Handle => _mpv;

    /// <summary>Gets the render context handle. Set by MpvView after creating it.</summary>
    internal IntPtr RenderContextHandle => _renderContext;

    /// <summary>
    /// True when the OpenGL render context has been created by MpvView
    /// and is ready to receive video frames. Playback commands (LoadFile,
    /// Play) should wait for this to be true — otherwise MPV starts
    /// decoding with no output surface, producing a black screen.
    /// </summary>
    /// <remarks>
    /// Prevent MPV from beginning playback before the UI has attached the render context.
    /// </remarks>
    public bool IsRenderContextReady { get; private set; }

    internal void SetRenderContext(IntPtr ctx)
    {
        _renderContext = ctx;
        AllocateRenderBuffers();
    }

    private void AllocateRenderBuffers()
    {
        FreeRenderBuffers();
        _renderParamSize = Marshal.SizeOf<MpvRenderParam>();
        _renderParamsPtr = Marshal.AllocHGlobal(_renderParamSize * 3);
        _renderFboPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MpvOpenglFbo>());
        _renderFlipYPtr = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(_renderFlipYPtr, 1); // flip Y = true (constant)
    }

    private void FreeRenderBuffers()
    {
        if (_renderParamsPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_renderParamsPtr); _renderParamsPtr = IntPtr.Zero; }
        if (_renderFboPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_renderFboPtr); _renderFboPtr = IntPtr.Zero; }
        if (_renderFlipYPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_renderFlipYPtr); _renderFlipYPtr = IntPtr.Zero; }
    }

    internal void MarkRenderContextReady()
    {
        IsRenderContextReady = true;
        _renderContextReadyTcs?.TrySetResult();
    }

    /// <summary>
    /// Wait for the render context to be ready (with timeout).
    /// Called by PlayVideoAsync before LoadFile.
    /// </summary>
    public async Task WaitForRenderContextReadyAsync(int timeoutMs = 2000)
    {
        if (IsRenderContextReady) return;

        _renderContextReadyTcs ??= new TaskCompletionSource();
        try
        {
            await _renderContextReadyTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }
        catch (TimeoutException)
        {
            Trace.WriteLine("[MPV] WaitForRenderContextReadyAsync timed out — proceeding anyway (may produce black first frame).");
        }
    }

    private TaskCompletionSource? _renderContextReadyTcs;

    /// <summary>
    /// Called when a property we're observing changes.
    /// Arguments: (propertyName, value).
    /// </summary>
    public event Action<string, object?>? PropertyChanged;

    /// <summary>
    /// Called when playback reaches the end of the file (eof-reached becomes true).
    /// </summary>
    public event Action? EndReached;

    /// <summary>
    /// Called once mpv has loaded a file and it is ready for playback.
    /// </summary>
    public event Action? FileLoaded;

    /// <summary>
    /// Create a new mpv handle and initialize it.
    /// </summary>
    public bool Initialize()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MpvContext));

        var sw = Stopwatch.StartNew();
        Trace.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] Initializing MPV...");

        try
        {
            _mpv = MpvNative.mpv_create();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MPV] mpv_create() threw exception: {ex.Message}");
            Trace.WriteLine($"[MPV] This usually means libmpv-2.dll / libmpv.so.2 / libmpv.2.dylib was not found.");
            return false;
        }

        if (_mpv == IntPtr.Zero)
        {
            Trace.WriteLine("[MPV] mpv_create() returned IntPtr.Zero — libmpv not found or failed to initialize.");
            Trace.WriteLine("[MPV] Check that libmpv is available:");
            Trace.WriteLine("[MPV]   Windows: place libmpv-2.dll in the lib/ folder next to Jukebox.exe");
            Trace.WriteLine("[MPV]   Linux:   place libmpv.so.2 in the lib/ folder, OR `sudo apt install libmpv-dev`");
            Trace.WriteLine("[MPV]   macOS:   brew install mpv (or place libmpv.2.dylib in lib/)");
            return false;
        }

        // Set options before initialize.
        SetOptionString("vo", "libmpv");        // Use the render API (no native window)
        SetOptionString("terminal", "no");       // Don't spam the terminal
        SetOptionString("msg-level", "all=no");  // Suppress log messages
        SetOptionString("keep-open", "yes");     // Don't close the file at EOF — we handle next-track ourselves

        int initResult = MpvNative.mpv_initialize(_mpv);
        if (initResult < 0)
        {
            Trace.WriteLine($"[MPV] mpv_initialize() failed with error code {initResult}.");
            Trace.WriteLine("[MPV] Common error codes: -1 = MPV_ERROR_UNINITIALIZED, -2 = MPV_ERROR_INVALID_PARAMETER, etc.");
            MpvNative.mpv_terminate_destroy(_mpv);
            _mpv = IntPtr.Zero;
            return false;
        }

        // Start the event thread to receive property-change notifications.
        _eventCts = new CancellationTokenSource();
        _eventThread = new Thread(() => EventLoop(_eventCts.Token))
        {
            IsBackground = true,
            Name = "MPV Event Thread"
        };
        _eventThread.Start();

        Trace.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] MPV initialized in {sw.ElapsedMilliseconds}ms.");
        return true;
    }

    /// <summary>
    /// Set an mpv option (must be called before Initialize for some options,
    /// after for others — see libmpv docs).
    /// </summary>
    public void SetOptionString(string name, string value)
    {
        if (_mpv == IntPtr.Zero) return;
        int result = MpvNative.mpv_set_option_string(_mpv, name, value);
        if (result < 0)
        {
            Trace.WriteLine($"[MPV] Could not set option '{name}' to '{value}' (error {result}).");
        }
    }

    // ── Playback commands ──

    /// <summary>Load a file for playback. Replaces any current file.</summary>
    public void LoadFile(string path)
    {
        if (_mpv == IntPtr.Zero) return;
        Command("loadfile", path, "replace");
    }

    /// <summary>Start or resume playback.</summary>
    public void Play() => SetProperty("pause", false);

    /// <summary>Pause playback.</summary>
    public void Pause() => SetProperty("pause", true);

    /// <summary>Stop playback and clear the current file.</summary>
    public void Stop() => Command("stop");

    /// <summary>Seek to an absolute position in seconds.</summary>
    public void SeekAbsolute(double seconds)
    {
        Command("seek", seconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "absolute");
    }

    // ── Property get/set ──

    /// <summary>Set a string property (e.g. "pause", "volume").</summary>
    public void SetProperty(string name, string value)
    {
        if (_mpv == IntPtr.Zero) return;
        MpvNative.mpv_set_property_string(_mpv, name, value);
    }

    /// <summary>Set a boolean property.</summary>
    public void SetProperty(string name, bool value) => SetProperty(name, value ? "yes" : "no");

    /// <summary>Set a double property (e.g. "volume").</summary>
    public void SetProperty(string name, double value)
    {
        if (_mpv == IntPtr.Zero) return;
        var v = value;
        MpvNative.mpv_set_property(_mpv, name, MpvFormat.Double, ref v);
    }

    /// <summary>Get a string property. Returns null if unavailable.</summary>
    public string? GetString(string name)
    {
        if (_mpv == IntPtr.Zero) return null;
        return MpvNative.mpv_get_property_string(_mpv, name);
    }

    /// <summary>Get a double property (e.g. "time-pos", "duration"). Returns null if unavailable.</summary>
    public double? GetDouble(string name)
    {
        if (_mpv == IntPtr.Zero) return null;
        double v = 0;
        if (MpvNative.mpv_get_property(_mpv, name, MpvFormat.Double, ref v) >= 0)
            return v;
        return null;
    }

    /// <summary>Get a boolean property.</summary>
    public bool? GetBool(string name)
    {
        var s = GetString(name);
        if (s == null) return null;
        return s == "yes" || s == "true";
    }

    // ── Property observation ──

    /// <summary>
    /// Observe a property. When it changes, <see cref="PropertyChanged"/>
    /// fires with the property name and new value (as a string).
    /// </summary>
    public void ObserveProperty(string name, MpvFormat format = MpvFormat.String)
    {
        if (_mpv == IntPtr.Zero) return;
        MpvNative.mpv_observe_property(_mpv, 0, name, format);
    }

    // ── Volume convenience ──

    /// <summary>Set volume (0-100).</summary>
    public void SetVolume(double volume) => SetProperty("volume", volume);

    /// <summary>Get current volume (0-100).</summary>
    public double GetVolume() => GetDouble("volume") ?? 100;

    // ── Position/duration convenience ──

    /// <summary>Current playback position in seconds, or null if unavailable.</summary>
    public double? GetPosition() => GetDouble("time-pos");

    /// <summary>Total duration in seconds, or null if unavailable.</summary>
    public double? GetDuration() => GetDouble("duration");

    // ── Internal command helper ──

    private void Command(params string[] args)
    {
        if (_mpv == IntPtr.Zero) return;

        // mpv_command takes a null-terminated array of null-terminated C strings.
        // Each string must be marshalled to unmanaged memory.
        var ptrs = new IntPtr[args.Length + 1];
        var allocatedStrings = new IntPtr[args.Length];
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                allocatedStrings[i] = Marshal.StringToHGlobalAnsi(args[i]);
                ptrs[i] = allocatedStrings[i];
            }
            ptrs[args.Length] = IntPtr.Zero; // null terminator
            MpvNative.mpv_command(_mpv, ptrs);
        }
        finally
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (allocatedStrings[i] != IntPtr.Zero)
                    Marshal.FreeHGlobal(allocatedStrings[i]);
            }
        }
    }

    // ── Event loop (background thread) ──
    //
    // Replaced hardcoded struct offsets and magic event ID with proper struct 
    // definitions and Marshal.PtrToStructure.
    //
    // Note on mpv_event layout on x64 (24 bytes total):
    //   offset 0:  event_id      (int, 4 bytes)
    //   offset 4:  error         (int, 4 bytes)
    //   offset 8:  reply_userdata (uint64, 8 bytes)
    //   offset 16: data          (void*, 8 bytes)
    //
    // The MPV_EVENT_PROPERTY_CHANGE event ID is 22 in libmpv 2.x (which is used 
    // by this application). The `data` field containing the property change details
    // is at offset 16.
    //
    // Combined effect: property changes (duration, time-pos, eof-reached)
    // were silently dropped. The app "worked" because:
    //   - Position updates: PlaybackTimer_Tick polls mpv_get_property("time-pos")
    //     directly, independent of events.
    //   - Duration: never displayed for video files (the user may not have
    //     noticed).
    //   - End-reached: video froze on last frame (keep-open=yes) without
    //     auto-advancing (the user may have clicked Next manually).
    //
    // The fix defines proper structs (MpvEvent, MpvEventProperty) and an
    // enum (MpvEventId) with correct values from client.h, then uses
    // Marshal.PtrToStructure to read them. This is self-documenting,
    // correct on every platform, and survives struct layout changes.

    private void EventLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _mpv != IntPtr.Zero)
        {
            // Wait for the next event (blocks until one is available).
            var eventPtr = MpvNative.mpv_wait_event(_mpv, 1.0);
            if (eventPtr == IntPtr.Zero) continue;

            // Read the mpv_event struct using Marshal.PtrToStructure.
            // The struct layout is defined by MpvEvent below — no more
            // hardcoded offsets.
            var evt = Marshal.PtrToStructure<MpvEvent>(eventPtr);

            if (evt.EventId == MpvEventId.FileLoaded)
            {
                Task.Run(() =>
                {
                    try { FileLoaded?.Invoke(); }
                    catch (Exception ex) { Debug.WriteLine($"[MPV] FileLoaded callback error: {ex.Message}"); }
                });
            }
            else if (evt.EventId == MpvEventId.PropertyChange)
            {
                // evt.Data points to an mpv_event_property struct:
                //   { const char *name; mpv_format format; void *data; }
                if (evt.Data == IntPtr.Zero) continue;

                var prop = Marshal.PtrToStructure<MpvEventProperty>(evt.Data);
                var name = prop.Name != IntPtr.Zero
                    ? Marshal.PtrToStringAnsi(prop.Name)
                    : null;

                object? value = null;
                if (prop.Data != IntPtr.Zero)
                {
                    if (prop.Format == MpvFormat.Double)
                        value = Marshal.PtrToStructure<double>(prop.Data);
                    else if (prop.Format == MpvFormat.Flag)
                        value = Marshal.ReadByte(prop.Data) != 0;
                    else if (prop.Format == MpvFormat.String)
                    {
                        var strPtr = Marshal.ReadIntPtr(prop.Data);
                        if (strPtr != IntPtr.Zero)
                            value = Marshal.PtrToStringAnsi(strPtr);
                    }
                }

                if (name != null)
                {
                    // Marshal to UI thread via Task.Run. The subscriber
                    // (MpvPlaybackEngine.OnMpvPropertyChanged) forwards
                    // to DurationChanged, and JukeboxViewModel.OnEngineDurationChanged
                    // dispatches to the UI thread via Dispatcher.UIThread.Post.
                    // So this is double-dispatched, but that's safe — the
                    // extra Task.Run just decouples from the MPV event thread.
                    Task.Run(() =>
                    {
                        try { PropertyChanged?.Invoke(name, value); }
                        catch (Exception ex) { Debug.WriteLine($"[MPV] PropertyChanged callback error: {ex.Message}"); }
                    });

                    if (name == "eof-reached" && value is bool b && b)
                    {
                        Task.Run(() =>
                        {
                            try { EndReached?.Invoke(); }
                            catch (Exception ex) { Debug.WriteLine($"[MPV] EndReached callback error: {ex.Message}"); }
                        });
                    }
                }
            }
        }
    }

    // ── Render context support (used by MpvView) ──

    /// <summary>
    /// The get_proc_address delegate — must be kept alive for the lifetime
    /// of the render context. MpvView sets this before creating the render context.
    /// </summary>
    internal MpvNative.MpvGetProcAddressDelegate GetProcAddressDelegate
    {
        get => _getProcAddressDelegate!;
        set => _getProcAddressDelegate = value;
    }

    /// <summary>
    /// Request a render update from the render context (called by MpvView's
    /// update callback). Returns true if a new frame is available.
    /// </summary>
    internal bool CheckRenderUpdate()
    {
        if (_disposed || _renderContext == IntPtr.Zero) return false;
        var flags = MpvNative.mpv_render_context_update(_renderContext);
        return (flags & MpvNative.MPV_RENDER_UPDATE_FRAME) != 0;
    }

    /// <summary>
    /// Render the current frame into the specified OpenGL FBO.
    /// Called from MpvView.OnOpenGlRender (on the GL render thread).
    /// </summary>
    internal void Render(int fbo, int width, int height)
    {
        // Guard against rendering after disposal — the update callback
        // might have already been queued before we nulled it.
        if (_disposed || _renderContext == IntPtr.Zero || _mpv == IntPtr.Zero) return;
        if (_renderParamsPtr == IntPtr.Zero) return;

        // Write per-frame FBO data into the pre-allocated buffer.
        var fboStruct = new MpvOpenglFbo { Fbo = fbo, W = width, H = height, InternalFormat = 0 };
        Marshal.StructureToPtr(fboStruct, _renderFboPtr, false);

        // Build the render parameter array (flip Y is pre-written as 1):
        // [0] MPV_RENDER_PARAM_OPENGL_FBO → pre-allocated fbo struct
        // [1] MPV_RENDER_PARAM_FLIP_Y → pre-allocated int (constant 1)
        // [2] MPV_RENDER_PARAM_INVALID → terminator
        Marshal.WriteInt32(_renderParamsPtr + 0 * _renderParamSize, MpvNative.MPV_RENDER_PARAM_OPENGL_FBO);
        Marshal.WriteIntPtr(_renderParamsPtr + 0 * _renderParamSize + IntPtr.Size, _renderFboPtr);

        Marshal.WriteInt32(_renderParamsPtr + 1 * _renderParamSize, MpvNative.MPV_RENDER_PARAM_FLIP_Y);
        Marshal.WriteIntPtr(_renderParamsPtr + 1 * _renderParamSize + IntPtr.Size, _renderFlipYPtr);

        Marshal.WriteInt32(_renderParamsPtr + 2 * _renderParamSize, MpvNative.MPV_RENDER_PARAM_INVALID);
        Marshal.WriteIntPtr(_renderParamsPtr + 2 * _renderParamSize + IntPtr.Size, IntPtr.Zero);

        MpvNative.mpv_render_context_render(_renderContext, _renderParamsPtr);
    }

    // ── Dispose ──

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Stop the event thread first.
        _eventCts?.Cancel();
        _eventCts?.Dispose();
        MpvNative.mpv_wakeup(_mpv);

        // ── Critical: null the update callback BEFORE freeing the render
        // context. If we don't, MPV's internal thread may call the callback
        // after the context is freed, causing AccessViolationException. ──
        if (_renderContext != IntPtr.Zero)
        {
            try
            {
                MpvNative.mpv_render_context_set_update_callback(
                    _renderContext, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception ex) { Trace.WriteLine($"[MPV] set_update_callback(null) failed: {ex.Message}"); }
        }

        // Give MPV's internal threads a moment to notice the null callback
        // and stop calling it. Without this, a race between the callback
        // thread and render_context_free can still crash.
        System.Threading.Thread.Sleep(50);

        // Now safe to free the render context.
        if (_renderContext != IntPtr.Zero)
        {
            try { MpvNative.mpv_render_context_free(_renderContext); }
            catch (Exception ex) { Trace.WriteLine($"[MPV] render_context_free failed: {ex.Message}"); }
            _renderContext = IntPtr.Zero;
        }

        // Terminate the mpv handle.
        if (_mpv != IntPtr.Zero)
        {
            try { MpvNative.mpv_terminate_destroy(_mpv); }
            catch (Exception ex) { Debug.WriteLine($"[MPV] terminate_destroy failed: {ex.Message}"); }
            _mpv = IntPtr.Zero;
        }

        FreeRenderBuffers();
    }

    // ── Native structs ──
    //
    // Struct definitions matching libmpv's client.h.

    /// <summary>
    /// libmpv event IDs. Values from mpv/client.h (libmpv 2.x API).
    /// </summary>
    internal enum MpvEventId
    {
        /// <summary>No event. Used internally.</summary>
        None = 0,
        /// <summary>Playback was shut down.</summary>
        Shutdown = 1,
        /// <summary>A log message was received.</summary>
        LogMessage = 2,
        /// <summary>Reply to a mpv_get_property_async request.</summary>
        GetPropertyReply = 3,
        /// <summary>Reply to a mpv_set_property_async request.</summary>
        SetPropertyReply = 4,
        /// <summary>Reply to a mpv_command_async request.</summary>
        CommandReply = 5,
        /// <summary>A new file has started loading.</summary>
        StartFile = 6,
        /// <summary>A file has finished loading (or was unloaded).</summary>
        EndFile = 7,
        /// <summary>The file has been loaded and is ready for playback.</summary>
        FileLoaded = 8,
        // Events 9-10 are deprecated/removed in current API.
        /// <summary>The player has entered idle mode (no file loaded).</summary>
        Idle = 11,
        // Event 12 is deprecated.
        // Event 13 is deprecated (was MPV_EVENT_PROPERTY_CHANGE in very old
        // API versions — now 22). The old code checked for 13, which never
        // matched in libmpv 2.x.
        /// <summary>Triggered periodically during playback (e.g. for UI updates).</summary>
        Tick = 14,
        // Events 15 is deprecated.
        /// <summary>A client message was received (custom protocol).</summary>
        ClientMessage = 16,
        /// <summary>Video parameters changed (resolution, format, etc.).</summary>
        VideoReconfig = 17,
        /// <summary>Audio parameters changed (sample rate, channels, etc.).</summary>
        AudioReconfig = 18,
        // Event 19 is deprecated.
        /// <summary>A seek operation was initiated.</summary>
        Seek = 20,
        /// <summary>Playback was restarted after a seek or pause.</summary>
        PlaybackRestart = 21,
        /// <summary>An observed property changed value. This is the event
        /// we process in EventLoop to feed PropertyChanged/EndReached.</summary>
        PropertyChange = 22,
        // Event 23 is deprecated.
        /// <summary>The event queue overflowed (events were dropped).</summary>
        QueueOverflow = 24,
        /// <summary>A hook was triggered (used for synchronous client integration).</summary>
        Hook = 25,
    }

    /// <summary>
    /// Native mpv_event struct. Layout from mpv/client.h:
    /// <code>
    /// typedef struct mpv_event {
    ///     mpv_event_id event_id;      // int
    ///     int error;
    ///     uint64_t reply_userdata;
    ///     void *data;
    /// } mpv_event;
    /// </code>
    /// On x64: 4 + 4 + 8 + 8 = 24 bytes, with `data` at offset 16.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvEvent
    {
        public MpvEventId EventId;
        public int Error;
        public ulong ReplyUserdata;
        public IntPtr Data;
    }

    /// <summary>
    /// Native mpv_event_property struct. Layout from mpv/client.h:
    /// <code>
    /// typedef struct mpv_event_property {
    ///     const char *name;
    ///     mpv_format format;
    ///     void *data;
    /// } mpv_event_property;
    /// </code>
    /// On x64: 8 + 4 + 4(pad) + 8 = 24 bytes, with `data` at offset 16
    /// (after 4 bytes of padding for 8-byte alignment).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvEventProperty
    {
        public IntPtr Name;        // const char*
        public MpvFormat Format;   // int (mpv_format)
        public IntPtr Data;        // void*
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvRenderParam
    {
        public int Type;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvOpenglFbo
    {
        public int Fbo;
        public int W;
        public int H;
        public int InternalFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvOpenglInitParams
    {
        public MpvNative.MpvGetProcAddressDelegate GetProcAddress;
        public IntPtr GetProcAddressCtx;
    }
}
