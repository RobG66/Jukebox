using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Jukebox.Native;

// ── BassNative ──────────────────────────────────────────────────────────────
//
// Hand-rolled P/Invoke wrapper for the native BASS audio library.
// Replaces the ManagedBass NuGet package (v4.0.2).
//
// Only the subset of the BASS API actually used by BassPlaybackEngine and
// VgmPlaybackEngine is declared here — approximately 25 native functions.
// Values are verified against the official BASS C headers from un4seen.com.
//
// Calling convention: all [DllImport] declarations intentionally leave
// CallingConvention unset (defaults to Winapi). BASS uses Winapi (stdcall)
// on 32-bit Windows and the platform default (effectively cdecl / System V
// ABI) on x64/Linux. Leaving it unset is what ManagedBass itself does and
// is correct for all supported platforms. Do NOT pin it to Cdecl.
//
// Library loading: BassNative.EnsureLoaded() must be called before any
// BASS function is invoked. It loads bass.dll (or libbass.so / libbass.dylib)
// and bass_fx.dll from the app's lib/ folder. Because all [DllImport]
// declarations are now in the Jukebox assembly, we only need one
// SetDllImportResolver on this assembly — no cross-assembly dance needed.
// ────────────────────────────────────────────────────────────────────────────

internal static class BassNative
{
    #region Fields & Constants

    // BASS_STREAM_STATUS: allows BASS to report download progress
    // (harmless flag for BASS_StreamCreateFileUser).
    internal const int STREAMFILE_BUFFER = 1;
    internal const int BASS_STREAM_STATUS = 0x800000;

    // BASS_STREAM_BLOCK: download/render in blocks. Used for BASS_StreamCreateURL.
    // For HLS we don't use this — segments are short enough.
    internal const int BASS_STREAM_BLOCK = 0x100000;

    // Sentinel passed to BASS_StreamCreate to create a push stream.
    // BASS does not call a DSP procedure for push streams — it expects data
    // fed via BASS_StreamPutData. Passing IntPtr(-1) signals this.
    private static readonly IntPtr PUSH_STREAM_SENTINEL = new IntPtr(-1);

    // BASS_CHANNELPOS_BYTE: position mode for ChannelGet/SetPosition.
    private const int BASS_POS_BYTE = 0;

    // BASS_UNICODE flag for PluginLoad and StreamCreateFile on Windows.
    private const int BASS_UNICODE = unchecked((int)0x80000000);

    // BASS_STREAM_BLOCK: used for BASS_StreamCreateFile to block on read.
    // Not currently needed — kept for documentation.
    // private const int BASS_STREAM_BLOCK = 0x100000;

    private static bool _loadAttempted;
    private static IntPtr _bassHandle;
    private static IntPtr _bassFxHandle;
    private static readonly object _loadLock = new();

    #endregion

    #region Enums

    // BASS_SetConfig option constants (bass.h BASS_CONFIG_xxx).
    internal enum BassConfiguration
    {
        PlaybackBufferLength = 8,   // BASS_CONFIG_BUFFER
        UpdatePeriod         = 9,   // BASS_CONFIG_UPDATEPERIOD
        NetTimeout           = 14,  // BASS_CONFIG_NET_TIMEOUT — seconds (0 = no timeout)
        NetAgent             = 16,  // BASS_CONFIG_NET_AGENT — User-Agent string (pointer)
        NetProxy             = 17,  // BASS_CONFIG_NET_PROXY — proxy URL string (pointer)
    }

    // Flags for BASS_Init.
    internal enum BassInitFlags
    {
        Default = 0,
    }

    // Flags for stream creation / channel operations.
    internal enum BassFlags
    {
        Default = 0,
    }

    // BASS_ChannelSetAttribute attribute types (bass.h BASS_ATTRIB_xxx).
    internal enum BassChannelAttribute
    {
        Volume = 2,     // BASS_ATTRIB_VOL
    }

    // BASS_ChannelSetSync type flags (bass.h BASS_SYNC_xxx).
    internal enum BassSyncFlags
    {
        End              = 2,       // BASS_SYNC_END
        MetadataReceived = 0x20000, // BASS_SYNC_META
    }

    // BASS_ChannelGetTags type constants (bass.h BASS_TAG_xxx).
    internal enum BassTagType
    {
        META = 0x11001, // BASS_TAG_META — ICY/Shoutcast in-stream metadata
    }

    // BASS_FX effect type for PeakEQ (bass_fx.h BASS_FX_BFX_PEAKEQ).
    internal enum BassEffectType
    {
        PeakEQ = 0x10004, // BASS_FX_BFX_PEAKEQ
    }

    // BASS error codes (bass.h BASS_ERROR_xxx).
    internal enum BassErrors
    {
        OK          = 0,
        Memory      = 1,  // BASS_ERROR_MEM
        FileOpen    = 2,  // BASS_ERROR_FILEOPEN
        Driver      = 3,  // BASS_ERROR_DRIVER
        BufferLost  = 4,  // BASS_ERROR_BUFLOST
        Handle      = 5,  // BASS_ERROR_HANDLE
        Format      = 6,  // BASS_ERROR_FORMAT
        Position    = 7,  // BASS_ERROR_POSITION
        Init        = 8,  // BASS_ERROR_INIT
        Start       = 9,  // BASS_ERROR_START
        SSL         = 10, // BASS_ERROR_SSL
        NoChannel   = 14, // BASS_ERROR_NOCHAN
        Timeout     = 23, // BASS_ERROR_TIMEOUT
        FileFormat  = 41, // BASS_ERROR_FILEFORM
        Speaker     = 42, // BASS_ERROR_SPEAKER
        Version     = 43, // BASS_ERROR_VERSION
        Codec       = 44, // BASS_ERROR_CODEC
        Ended       = 48, // BASS_ERROR_ENDED
        Busy        = 46, // BASS_ERROR_BUSY
        Already     = 82, // BASS_ERROR_ALREADY (device already initialized)
        Unknown     = -1, // BASS_ERROR_UNKNOWN
    }

    #endregion

    #region Structs & Delegates

    // BASS_CHANNELINFO struct layout (bass.h).
    // Field order is exact — getting it wrong causes ChannelGetInfo to return
    // plausible-looking garbage from shifted memory.
    [StructLayout(LayoutKind.Sequential)]
    internal struct BassChannelInfo
    {
        public int Frequency;   // freq
        public int Channels;    // chans
        public int Flags;       // flags (BassFlags bits)
        public int ChannelType; // ctype (BASS_CTYPE_xxx)
        public int OrigRes;     // origres
        public int Plugin;      // plugin handle (0 = built-in decoder)
        public int Sample;      // sample handle
        public IntPtr FileName; // pointer to filename (for file streams)
    }

    // BASS_BFX_PEAKEQ parameter struct (bass_fx.h).
    // Field order confirmed against bass_fx.h and multiple independent
    // bindings (Radio42, Genotrance):
    //   lBand, fBandwidth, fQ, fCenter, fGain, lChannel
    // 24 bytes total: 2 ints (4 bytes each) + 4 floats (4 bytes each).
    // lChannel = -1 means BASS_BFX_CHANALL (apply to all channels).
    [StructLayout(LayoutKind.Sequential)]
    internal struct PeakEqParams
    {
        public int   lBand;
        public float fBandwidth;
        public float fQ;
        public float fCenter;
        public float fGain;
        public int   lChannel;
    }

    // BASS_FILEPROCS callbacks for BASS_StreamCreateFileUser.
    // Kept in BassNative so both the struct and the DllImport are co-located.
    [StructLayout(LayoutKind.Sequential)]
    internal struct BASS_FILEPROCS
    {
        public BassFileProcClose  close;
        public BassFileProcLength length;
        public BassFileProcRead   read;
        public BassFileProcSeek   seek;
    }

    internal delegate void   BassFileProcClose(IntPtr user);
    internal delegate long   BassFileProcLength(IntPtr user);
    internal delegate int    BassFileProcRead(IntPtr buffer, int length, IntPtr user);
    internal delegate bool   BassFileProcSeek(long offset, IntPtr user);

    // DSP callback — fired by BASS for every audio buffer rendered.
    internal delegate void BassDspProcedure(int handle, int channel, IntPtr buffer, int length, IntPtr user);

    // Sync callback — fired by BASS for sync events (end-of-stream, metadata).
    internal delegate void BassSyncProcedure(int handle, int channel, int data, IntPtr user);

    // Download callback for BASS_StreamCreateURL — called as data is downloaded.
    // We don't use this (pass NULL), but the delegate type must exist for the
    // P/Invoke signature to be valid.
    internal delegate void BassDownloadProcedure(IntPtr buffer, int length, IntPtr user);

    #endregion

    #region Library Loading

    // Call once before any BASS function. Loads bass.dll / libbass.so and
    // bass_fx.dll / libbass_fx.so from lib/, then registers a resolver on
    // the Jukebox assembly so [DllImport("bass")] / [DllImport("bass_fx")]
    // calls in this file resolve to those handles on Linux (where NativeLibrary
    // handles are assembly-scoped, unlike Windows).
    internal static void EnsureLoaded()
    {
        if (_loadAttempted) return;
        lock (_loadLock)
        {
            if (_loadAttempted) return;
            _loadAttempted = true;

            string libDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib");

            // Load bass (core).
            string bassFile = NativeFileName("bass", "libbass");
            string bassPath = Path.Combine(libDir, bassFile);
            if (File.Exists(bassPath) && NativeLibrary.TryLoad(bassPath, out _bassHandle))
                Debug.WriteLine($"[BassNative] Loaded BASS from: {bassPath}");
            else if (!NativeLibrary.TryLoad(bassFile, out _bassHandle))
                Debug.WriteLine($"[BassNative] BASS not found. Looked in: {bassPath} and OS search path.");

            // Load bass_fx (EQ plugin — optional, but EQ won't work without it).
            string fxFile = NativeFileName("bass_fx", "libbass_fx");
            string fxPath = Path.Combine(libDir, fxFile);
            if (File.Exists(fxPath) && NativeLibrary.TryLoad(fxPath, out _bassFxHandle))
                Debug.WriteLine($"[BassNative] Loaded BASS_FX from: {fxPath}");
            else if (!NativeLibrary.TryLoad(fxFile, out _bassFxHandle))
                Debug.WriteLine($"[BassNative] BASS_FX not found — EQ will be unavailable. Looked in: {fxPath}");

            // Register a resolver on the Jukebox assembly (the assembly that
            // contains these [DllImport] declarations). On Linux, NativeLibrary
            // handles are scoped per-assembly — without this resolver, the
            // P/Invoke runtime would ignore our already-loaded handle and try
            // the OS search path (which doesn't include our lib/ folder).
            // On Windows this is redundant (handles are process-global) but
            // harmless — the resolver is checked first and returns our handle.
            NativeLibrary.SetDllImportResolver(typeof(BassNative).Assembly, (name, _, _) =>
            {
                if (name == "bass") return _bassHandle;
                if (name == "bass_fx") return _bassFxHandle;
                return IntPtr.Zero;
            });
        }
    }

    // Preloads a BASS plugin (basshls.dll, bass_aac.dll, etc.) into the
    // process address space before registering it with BASS_PluginLoad.
    // Returns the BASS plugin handle (0 on failure).
    internal static int LoadPlugin(string absolutePath)
    {
        if (!File.Exists(absolutePath)) return 0;

        // Pre-load so the OS linker finds the file regardless of working directory.
        if (NativeLibrary.TryLoad(absolutePath, out _))
            Debug.WriteLine($"[BassNative] Pre-loaded plugin via NativeLibrary: {absolutePath}");

        int handle = _PluginLoad(absolutePath, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? BASS_UNICODE : 0);
        if (handle != 0)
            Debug.WriteLine($"[BassNative] BASS registered plugin: {absolutePath}");
        else
            Debug.WriteLine($"[BassNative] BASS failed to register plugin: {absolutePath}. Error: {GetLastError()}");

        return handle;
    }

    private static string NativeFileName(string windowsName, string unixBaseName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))  return $"{windowsName}.dll";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))    return $"{unixBaseName}.so";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))      return $"{unixBaseName}.dylib";
        return windowsName;
    }

    #endregion

    #region Public API — Initialization

    // Returns the last BASS error code.
    internal static BassErrors GetLastError() => _ErrorGetCode();

    // Initialize a BASS output device.
    // device = -1 → default output device.
    // device =  0 → no-sound/decode-only device (silent!).
    // All 5 parameters must be present — omitting clsid shifts the device
    // argument in the native call and can silently select the silent device 0.
    internal static bool Init(int device, int freq, BassInitFlags flags, IntPtr win, IntPtr clsid)
        => _Init(device, freq, flags, win, clsid);

    // Release the current output device and free BASS resources.
    internal static bool Free() => _Free();

    // Set a BASS global configuration value.
    internal static bool Configure(BassConfiguration option, int value)
        => _SetConfig((int)option, value);

    #endregion

    #region Public API — Streams & Channels

    // Open a local audio file as a BASS stream.
    // On Windows, adds BASS_UNICODE so the path is passed as UTF-16.
    // On Linux/macOS, passes the path as-is (ANSI/UTF-8).
    internal static int CreateStream(string filePath, BassFlags flags)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return _StreamCreateFileW(false, filePath, 0, 0, (int)flags | BASS_UNICODE);
        else
            return _StreamCreateFile(false, filePath, 0, 0, (int)flags);
    }

    // Open an internet URL (HTTP/HTTPS, including HLS .m3u8) as a BASS stream.
    // Uses BASS_StreamCreateURL — the documented function for URL streaming.
    // BASS_StreamCreateFile does NOT support URLs (returns BASS_ERROR_ILLPARAM=20).
    //
    // When basshls plugin is loaded, it intercepts HLS URLs and handles playlist
    // parsing + segment fetching internally.
    //
    // The download callback (DOWNLOADPROC) is passed as NULL — we don't need
    // download progress notifications for HLS (segments are short).
    //
    // offset = 0 (no offset into the downloaded data).
    // flags = 0 (no special flags — BASS_STREAM_BLOCK would force blocking
    //   download which we don't want for live HLS streams).
    internal static int CreateUrlStream(string url)
    {
        int flags = 0;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return _StreamCreateURLW(url, 0, flags | BASS_UNICODE, IntPtr.Zero, IntPtr.Zero);
        else
            return _StreamCreateURLA(url, 0, flags, IntPtr.Zero, IntPtr.Zero);
    }

    // Create a BASS push stream. Data is fed by the caller via StreamPutData.
    // Uses the IntPtr(-1) sentinel for the proc argument — BASS_StreamCreate
    // with a sentinel means "push mode, no callback".
    internal static int CreatePushStream(int freq, int channels, BassFlags flags)
        => _StreamCreate(freq, channels, (int)flags, PUSH_STREAM_SENTINEL, IntPtr.Zero);

    // Create a BASS user-file stream (used for HttpClient-fed URL streams).
    internal static int StreamCreateFileUser(int system, int flags, ref BASS_FILEPROCS procs, IntPtr user)
        => _StreamCreateFileUser(system, flags, ref procs, user);

    // Free a BASS stream handle.
    internal static bool StreamFree(int handle) => _StreamFree(handle);

    // Push decoded audio data into a push stream.
    // Returns free space remaining in the buffer, or -1 on error.
    // Pass IntPtr.Zero with length=0x80000000 to signal end-of-stream.
    internal static int StreamPutData(int handle, IntPtr buffer, int length)
        => _StreamPutData(handle, buffer, length);

    // Start/resume playback on a channel. restart=false resumes from current position.
    internal static bool ChannelPlay(int handle, bool restart = false)
        => _ChannelPlay(handle, restart);

    // Pause playback on a channel.
    internal static bool ChannelPause(int handle) => _ChannelPause(handle);

    // Set playback position in bytes. mode=BASS_POS_BYTE (0) is the standard byte position.
    internal static bool ChannelSetPosition(int handle, long position, int mode = BASS_POS_BYTE)
        => _ChannelSetPosition(handle, position, mode);

    // Get current playback position in bytes.
    internal static long ChannelGetPosition(int handle, int mode = BASS_POS_BYTE)
        => _ChannelGetPosition(handle, mode);

    // Get the total byte length of a channel (for finite streams).
    internal static long ChannelGetLength(int handle, int mode = BASS_POS_BYTE)
        => _ChannelGetLength(handle, mode);

    // Convert a byte position to seconds.
    internal static double ChannelBytes2Seconds(int handle, long position)
        => _ChannelBytes2Seconds(handle, position);

    // Convert seconds to a byte position.
    internal static long ChannelSeconds2Bytes(int handle, double seconds)
        => _ChannelSeconds2Bytes(handle, seconds);

    // Set a channel attribute. Accepts double for convenience, casts to float
    // before the native call. BASS_ChannelSetAttribute's third parameter is a
    // native float — passing a double directly would push 8 bytes where the
    // runtime reads 4, corrupting the value.
    internal static bool ChannelSetAttribute(int handle, BassChannelAttribute attrib, double value)
        => _ChannelSetAttribute(handle, (int)attrib, (float)value);

    // Get channel information (sample rate, channels, etc.).
    internal static bool GetChannelInfo(int handle, out BassChannelInfo info)
        => _ChannelGetInfo(handle, out info);

    // Get a pointer to a channel tag string (e.g. ICY metadata).
    internal static IntPtr ChannelGetTags(int handle, BassTagType tagType)
        => _ChannelGetTags(handle, (int)tagType);

    #endregion

    #region Public API — DSP & Sync

    // Attach a DSP function to a channel (called for each rendered buffer).
    // priority=0 is standard (higher runs earlier in the chain).
    internal static int ChannelSetDSP(int handle, BassDspProcedure proc, IntPtr user, int priority)
        => _ChannelSetDSP(handle, proc, user, priority);

    // Remove a DSP function from a channel.
    internal static bool ChannelRemoveDSP(int handle, int dspHandle)
        => _ChannelRemoveDSP(handle, dspHandle);

    // Attach a sync callback to a channel (fired on end-of-stream, metadata, etc.).
    internal static int ChannelSetSync(int handle, BassSyncFlags type, long param, BassSyncProcedure proc, IntPtr user)
        => _ChannelSetSync(handle, (int)type, param, proc, user);

    // Remove a sync callback from a channel.
    internal static bool ChannelRemoveSync(int handle, int syncHandle)
        => _ChannelRemoveSync(handle, syncHandle);

    #endregion

    #region Public API — Effects (EQ)

    // Attach an effect to a channel. Returns the effect handle (0 on failure).
    // priority=0 is standard.
    // BASS_ChannelSetFX lives in bass.dll (core), not bass_fx.dll — the
    // bass_fx library adds new effect types but reuses the core dispatch.
    internal static int ChannelSetFX(int handle, BassEffectType type, int priority)
        => _ChannelSetFX(handle, (int)type, priority);

    // Remove an effect from a channel.
    internal static bool ChannelRemoveFX(int handle, int fxHandle)
        => _ChannelRemoveFX(handle, fxHandle);

    // Set effect parameters via a raw pointer to the parameter struct.
    // BASS_FXSetParameters also lives in bass.dll, not bass_fx.dll.
    internal static bool FXSetParameters(int handle, IntPtr parameters)
        => _FXSetParameters(handle, parameters);

    #endregion

    #region P/Invoke — bass.dll declarations

    // Init / teardown.
    [DllImport("bass", EntryPoint = "BASS_Init")]
    private static extern bool _Init(int device, int freq, BassInitFlags flags, IntPtr win, IntPtr clsid);

    [DllImport("bass", EntryPoint = "BASS_Free")]
    private static extern bool _Free();

    [DllImport("bass", EntryPoint = "BASS_SetConfig")]
    private static extern bool _SetConfig(int option, int value);

    // BASS_SetConfigPtr — sets a pointer-based config option (User-Agent, proxy).
    // BASS stores the pointer (does NOT copy the string), so the caller must
    // keep the memory alive until the next call to set the same option.
    [DllImport("bass", EntryPoint = "BASS_SetConfigPtr", CharSet = CharSet.Ansi)]
    private static extern bool _SetConfigPtr(int option, string value);

    internal static bool SetConfigPtr(BassConfiguration option, string value)
        => _SetConfigPtr((int)option, value);

    [DllImport("bass", EntryPoint = "BASS_ErrorGetCode")]
    private static extern BassErrors _ErrorGetCode();

    // Plugin loading. Two overloads: Unicode (Windows) and ANSI (Linux/macOS).
    [DllImport("bass", EntryPoint = "BASS_PluginLoad", CharSet = CharSet.Unicode)]
    private static extern int _PluginLoadW(string file, int flags);

    [DllImport("bass", EntryPoint = "BASS_PluginLoad")]
    private static extern int _PluginLoadA([MarshalAs(UnmanagedType.LPStr)] string file, int flags);

    private static int _PluginLoad(string file, int flags) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? _PluginLoadW(file, flags)
            : _PluginLoadA(file, flags);

    // Stream creation — local file (Unicode for Windows, ANSI for Linux).
    [DllImport("bass", EntryPoint = "BASS_StreamCreateFile", CharSet = CharSet.Unicode)]
    private static extern int _StreamCreateFileW(
        [MarshalAs(UnmanagedType.Bool)] bool mem,
        [MarshalAs(UnmanagedType.LPWStr)] string file,
        long offset, long length, int flags);

    [DllImport("bass", EntryPoint = "BASS_StreamCreateFile")]
    private static extern int _StreamCreateFile(
        [MarshalAs(UnmanagedType.Bool)] bool mem,
        [MarshalAs(UnmanagedType.LPStr)] string file,
        long offset, long length, int flags);

    // BASS_StreamCreateURL — for internet URLs (HTTP/HTTPS/HLS).
    // Two overloads: Unicode (Windows) and ANSI (Linux/macOS), matching the
    // _StreamCreateFile pattern.
    //
    // The DOWNLOADPROC parameter is declared as IntPtr (not BassDownloadProcedure)
    // so we can pass IntPtr.Zero when no download callback is wanted. Passing
    // null for a delegate parameter in P/Invoke doesn't reliably translate to
    // a null function pointer — the runtime may pass a garbage thunk instead,
    // which BASS rejects with BASS_ERROR_ILLPARAM (20).
    [DllImport("bass", EntryPoint = "BASS_StreamCreateURL", CharSet = CharSet.Unicode)]
    private static extern int _StreamCreateURLW(
        [MarshalAs(UnmanagedType.LPWStr)] string url,
        int offset, int flags,
        IntPtr proc, IntPtr user);

    [DllImport("bass", EntryPoint = "BASS_StreamCreateURL")]
    private static extern int _StreamCreateURLA(
        [MarshalAs(UnmanagedType.LPStr)] string url,
        int offset, int flags,
        IntPtr proc, IntPtr user);

    // Stream creation — push stream (sentinel proc = IntPtr(-1)).
    // Separate overload from the delegate version because passing a delegate
    // typed as a StreamProcedure for the sentinel value is undefined behaviour.
    [DllImport("bass", EntryPoint = "BASS_StreamCreate")]
    private static extern int _StreamCreate(int freq, int chans, int flags, IntPtr proc, IntPtr user);

    // Stream creation — user-file (HttpClient-fed URL streams).
    [DllImport("bass", EntryPoint = "BASS_StreamCreateFileUser")]
    private static extern int _StreamCreateFileUser(
        int system, int flags, ref BASS_FILEPROCS procs, IntPtr user);

    // Stream management.
    [DllImport("bass", EntryPoint = "BASS_StreamFree")]
    private static extern bool _StreamFree(int handle);

    [DllImport("bass", EntryPoint = "BASS_StreamPutData")]
    private static extern int _StreamPutData(int handle, IntPtr buffer, int length);

    // Channel playback.
    [DllImport("bass", EntryPoint = "BASS_ChannelPlay")]
    private static extern bool _ChannelPlay(int handle, [MarshalAs(UnmanagedType.Bool)] bool restart);

    [DllImport("bass", EntryPoint = "BASS_ChannelPause")]
    private static extern bool _ChannelPause(int handle);

    // Channel position / length.
    [DllImport("bass", EntryPoint = "BASS_ChannelSetPosition")]
    private static extern bool _ChannelSetPosition(int handle, long pos, int mode);

    [DllImport("bass", EntryPoint = "BASS_ChannelGetPosition")]
    private static extern long _ChannelGetPosition(int handle, int mode);

    [DllImport("bass", EntryPoint = "BASS_ChannelGetLength")]
    private static extern long _ChannelGetLength(int handle, int mode);

    [DllImport("bass", EntryPoint = "BASS_ChannelBytes2Seconds")]
    private static extern double _ChannelBytes2Seconds(int handle, long pos);

    [DllImport("bass", EntryPoint = "BASS_ChannelSeconds2Bytes")]
    private static extern long _ChannelSeconds2Bytes(int handle, double secs);

    // Channel attributes (volume etc.).
    // Third parameter is a native float — the public wrapper accepts double
    // and casts to float before this call.
    [DllImport("bass", EntryPoint = "BASS_ChannelSetAttribute")]
    private static extern bool _ChannelSetAttribute(int handle, int attrib, float value);

    // Channel info / tags.
    [DllImport("bass", EntryPoint = "BASS_ChannelGetInfo")]
    private static extern bool _ChannelGetInfo(int handle, out BassChannelInfo info);

    [DllImport("bass", EntryPoint = "BASS_ChannelGetTags")]
    private static extern IntPtr _ChannelGetTags(int handle, int tagType);

    // DSP.
    [DllImport("bass", EntryPoint = "BASS_ChannelSetDSP")]
    private static extern int _ChannelSetDSP(int handle, BassDspProcedure proc, IntPtr user, int priority);

    [DllImport("bass", EntryPoint = "BASS_ChannelRemoveDSP")]
    private static extern bool _ChannelRemoveDSP(int handle, int dsp);

    // Sync.
    [DllImport("bass", EntryPoint = "BASS_ChannelSetSync")]
    private static extern int _ChannelSetSync(int handle, int type, long param, BassSyncProcedure proc, IntPtr user);

    [DllImport("bass", EntryPoint = "BASS_ChannelRemoveSync")]
    private static extern bool _ChannelRemoveSync(int handle, int sync);

    // Effects (FX) — both live in bass.dll (core), not bass_fx.dll.
    // bass_fx.dll adds new effect types (PeakEQ, etc.) but the dispatch
    // functions BASS_ChannelSetFX and BASS_FXSetParameters remain in core.
    [DllImport("bass", EntryPoint = "BASS_ChannelSetFX")]
    private static extern int _ChannelSetFX(int handle, int type, int priority);

    [DllImport("bass", EntryPoint = "BASS_ChannelRemoveFX")]
    private static extern bool _ChannelRemoveFX(int handle, int fx);

    [DllImport("bass", EntryPoint = "BASS_FXSetParameters")]
    private static extern bool _FXSetParameters(int handle, IntPtr par);

    #endregion
}
