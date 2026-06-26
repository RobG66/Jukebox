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
    /// This is the root cause of the "first video is black" bug:
    /// https://github.com/damontecres/Wholphin/issues/576
    /// MPV begins playback before the UI has attached the render context.
    /// </remarks>
    public bool IsRenderContextReady { get; private set; }

    internal void SetRenderContext(IntPtr ctx)
    {
        _renderContext = ctx;
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
        MpvNative.mpv_set_property_string(_mpv, name, value);
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

    private void EventLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _mpv != IntPtr.Zero)
        {
            // Wait for the next event (blocks until one is available).
            var eventPtr = MpvNative.mpv_wait_event(_mpv, 1.0);
            if (eventPtr == IntPtr.Zero) continue;

            // mpv_event struct: { event_id (int), error (int), reply_userdata (ulong), data (void*) }
            // We only care about MPV_EVENT_PROPERTY_CHANGE (13) and MPV_EVENT_NONE (0).
            int eventId = Marshal.ReadInt32(eventPtr);
            if (eventId == 13) // MPV_EVENT_PROPERTY_CHANGE
            {
                // mpv_event_property: { name (char*), format (int), data (void*) }
                // Layout: name at offset 0 (8 bytes on x64), format at offset 8, data at offset 16.
                var dataPtr = Marshal.ReadIntPtr(eventPtr, 24); // offset of event.data in mpv_event
                if (dataPtr != IntPtr.Zero)
                {
                    var namePtr = Marshal.ReadIntPtr(dataPtr);
                    var format = Marshal.ReadInt32(dataPtr, IntPtr.Size);
                    var propDataPtr = Marshal.ReadIntPtr(dataPtr, IntPtr.Size + 8);

                    var name = namePtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(namePtr) : null;
                    object? value = null;

                    if (propDataPtr != IntPtr.Zero)
                    {
                        if ((MpvFormat)format == MpvFormat.Double)
                            value = Marshal.PtrToStructure<double>(propDataPtr);
                        else if ((MpvFormat)format == MpvFormat.Flag)
                            value = Marshal.ReadByte(propDataPtr) != 0;
                        else if ((MpvFormat)format == MpvFormat.String)
                        {
                            var strPtr = Marshal.ReadIntPtr(propDataPtr);
                            if (strPtr != IntPtr.Zero)
                                value = Marshal.PtrToStringAnsi(strPtr);
                        }
                    }

                    if (name != null)
                    {
                        // Marshal to UI thread.
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            try { PropertyChanged?.Invoke(name, value); }
                            catch (Exception ex) { Debug.WriteLine($"[MPV] PropertyChanged callback error: {ex.Message}"); }
                        });

                        if (name == "eof-reached" && value is bool b && b)
                        {
                            System.Threading.Tasks.Task.Run(() =>
                            {
                                try { EndReached?.Invoke(); }
                                catch (Exception ex) { Debug.WriteLine($"[MPV] EndReached callback error: {ex.Message}"); }
                            });
                        }
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

        // Build the render parameter array:
        // [0] MPV_RENDER_PARAM_OPENGL_FBO → mpv_opengl_fbo { fbo, w, h, internal_format=0 }
        // [1] MPV_RENDER_PARAM_FLIP_Y → 1 (Avalonia's FBO is upside-down)
        // [2] MPV_RENDER_PARAM_INVALID → terminator

        var fboStruct = new MpvOpenglFbo { Fbo = fbo, W = width, H = height, InternalFormat = 0 };
        var flipY = 1;

        var paramSize = Marshal.SizeOf<MpvRenderParam>();
        var paramsPtr = Marshal.AllocHGlobal(paramSize * 3);
        try
        {
            // Allocate the fbo struct in unmanaged memory (the param points to it).
            var fboPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MpvOpenglFbo>());
            Marshal.StructureToPtr(fboStruct, fboPtr, false);

            var flipYPtr = Marshal.AllocHGlobal(sizeof(int));
            Marshal.WriteInt32(flipYPtr, flipY);

            // param[0]: OpenGL FBO
            Marshal.WriteInt32(paramsPtr + 0 * paramSize, MpvNative.MPV_RENDER_PARAM_OPENGL_FBO);
            Marshal.WriteIntPtr(paramsPtr + 0 * paramSize + IntPtr.Size, fboPtr);

            // param[1]: Flip Y
            Marshal.WriteInt32(paramsPtr + 1 * paramSize, MpvNative.MPV_RENDER_PARAM_FLIP_Y);
            Marshal.WriteIntPtr(paramsPtr + 1 * paramSize + IntPtr.Size, flipYPtr);

            // param[2]: Invalid (terminator)
            Marshal.WriteInt32(paramsPtr + 2 * paramSize, MpvNative.MPV_RENDER_PARAM_INVALID);
            Marshal.WriteIntPtr(paramsPtr + 2 * paramSize + IntPtr.Size, IntPtr.Zero);

            MpvNative.mpv_render_context_render(_renderContext, paramsPtr);

            Marshal.FreeHGlobal(fboPtr);
            Marshal.FreeHGlobal(flipYPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(paramsPtr);
        }
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
    }

    // ── Native structs ──

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
