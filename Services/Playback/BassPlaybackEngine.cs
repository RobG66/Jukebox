using Jukebox.Models;
using ManagedBass;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Jukebox.Services;

public sealed class BassPlaybackEngine : IMediaPlayerEngine
{
    #region Fields & Constants
    private int _bassStream;

    // One-shot guard for PlaybackStarted. Set to 0 in PlayAsync/Stop, set to 1
    // the first time OnDsp fires after a successful ChannelPlay. Interlocked
    // CompareExchange makes the "first PCM buffer" check thread-safe: BASS's
    // DSP callback runs on BASS's audio thread, while Stop/PlayAsync run on
    // the UI thread.
    private int _playbackStartedFired;

    // PeakEQ (BASS_FX) uses one FX handle per channel and multiplexes bands
    // via the lBand field in BASS_BFX_PEAKEQ. The previous DXParamEQ approach
    // created one FX handle per band; PeakEQ creates one handle for all bands.
    private int _eqFxHandle;

    private DSPProcedure? _dspProcedure;
    private SyncProcedure? _endSyncProcedure;
    private int _dspHandle;
    private int _endSyncHandle;
    private bool _ownsBassContext;
    private double _volume = 100;
    private SyncProcedure? _metaSyncProcedure;
    private int _metaSyncHandle;

    // ── HttpClient-based URL streaming (BASS_StreamCreateFileUser) ──
    //
    // BASS's built-in HTTP client (Bass.CreateStream(url, ...)) has a quirk
    // where it stops reading after ~32KB when the server sends
    // "Connection: close" — which StreamTheWorld/Triton MediaGateway and
    // some other CDNs do. The server is still streaming (curl gets 230KB
    // in 30s), but BASS treats Connection: close as a finite download and
    // stops pulling data, firing the End sync after ~5 seconds.
    //
    // To fix this, we bypass BASS's HTTP client for URL streams. We use
    // .NET's HttpClient (which handles Connection: close correctly — it
    // keeps reading from the socket until the server actually closes) and
    // feed the bytes into BASS via BASS_StreamCreateFileUser. BASS still
    // detects the audio format (AAC, MP3, Opus) and decodes it through
    // its normal pipeline (Media Foundation / bass_aac / bassopus), so
    // EQ, visualizations, and ICY metadata all keep working.
    //
    // This approach is used for ALL URL streams — not just StreamTheWorld —
    // because it's more robust than BASS's built-in HTTP client and gives
    // us full control over the transport layer (User-Agent, redirects,
    // cookies, timeouts, etc.).
    //
    // IMPORTANT: The handler is configured to match what curl/MPV send:
    //   - UseCookies=true with a CookieContainer: required because the
    //     StreamTheWorld 302 redirect response sets session cookies (uuid,
    //     uuid-s) that identify us as a legitimate listener. Without these
    //     cookies on the redirected request, the server serves a 5-second
    //     preview/sample instead of the live stream.
    //   - AutomaticDecompression=None: HttpClient defaults to sending
    //     Accept-Encoding: gzip, deflate, which causes some CDNs to serve
    //     a compressed preview instead of the live stream. Disabling this
    //     makes HttpClient send no Accept-Encoding header (or identity),
    //     matching curl's default behavior.
    //   - AllowAutoRedirect=true: follows the 302 redirect from
    //     playerservices.streamtheworld.com to the actual CDN.
    private static readonly HttpClientHandler _httpHandler = new HttpClientHandler
    {
        CookieContainer = new System.Net.CookieContainer(),
        UseCookies = true,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
    };
    private static readonly HttpClient _httpClient = new HttpClient(_httpHandler)
    {
        Timeout = Timeout.InfiniteTimeSpan, // live streams have no timeout
    };
    private HttpResponseMessage? _httpResponse;
    private Stream? _networkStream;
    private CancellationTokenSource? _streamCts;

    // External cancellation token supplied by the caller (JukeboxViewModel).
    // When the caller starts a new track while a previous connection is still
    // opening, it cancels its CTS — linked into _streamCts in OpenUrlStreamAsync,
    // this propagates to _httpClient.SendAsync and aborts the in-flight HTTP
    // request. Defaults to CancellationToken.None (never cancels) until the
    // caller sets it via StreamCancellationToken.
    private CancellationToken _externalStreamCt = CancellationToken.None;
    private CancellationTokenSource? _linkedStreamCts;

    /// <summary>
    /// Sets the external cancellation token the engine should observe while
    /// opening URL streams. The token is linked into the internal CTS in
    /// <see cref="OpenUrlStreamAsync"/>; cancelling it aborts the HTTP send
    /// and propagates OperationCanceledException to the caller. Pass
    /// <see cref="CancellationToken.None"/> to clear.
    /// </summary>
    public void SetStreamCancellationToken(CancellationToken token)
    {
        _externalStreamCt = token;
    }

    private byte[]? _readBuffer;
    private DateTime _streamStartTimestamp;
    private long _streamBytesReceived;

    // ── ICY metadata (interleaved "Now Playing" title updates) ──
    //
    // _icyMetaInt: bytes of AUDIO data between metadata blocks, as reported
    // by the server's "icy-metaint" response header. 0 = server doesn't
    // support/offer ICY metadata for this stream.
    // _icyBytesUntilMeta: countdown to the next metadata block within the
    // current icy-metaint window.
    private int _icyMetaInt;
    private int _icyBytesUntilMeta;

    // BASS_StreamCreateFileUser callbacks. These MUST be kept as fields
    // (not local variables) so the GC doesn't collect them while BASS
    // still holds a pointer to the struct. If they get collected, BASS
    // will crash when it tries to call the callback.
    private BASS_FILEPROCS _fileProcs;
    private FileCloseProc? _fileCloseProc;
    private FileLenProc? _fileLenProc;
    private FileReadProc? _fileReadProc;
    private FileSeekProc? _fileSeekProc;

    // P/Invoke for BASS_StreamCreateFileUser.
    //
    // We use raw P/Invoke instead of ManagedBass's wrapper because the
    // wrapper's API surface for file-user streams varies across
    // ManagedBass versions. The native BASS API is stable and documented:
    //
    //   HSTREAM BASS_StreamCreateFileUser(
    //       DWORD system,        // STREAMFILE_NOBUFFER=0, STREAMFILE_BUFFER=1
    //       DWORD flags,         // BASS_STREAM_BLOCK, BASS_STREAM_STATUS, etc.
    //       BASS_FILEPROCS *procs,
    //       void *user
    //   );
    //
    // STREAMFILE_BUFFER (1) is used so BASS pre-buffers in a background
    // thread, matching how Bass.CreateStream(url) works internally.
    [DllImport("bass", EntryPoint = "BASS_StreamCreateFileUser")]
    private static extern int BASS_StreamCreateFileUser(
        int system,
        int flags,
        ref BASS_FILEPROCS procs,
        IntPtr user);

    [StructLayout(LayoutKind.Sequential)]
    private struct BASS_FILEPROCS
    {
        public FileCloseProc close;
        public FileLenProc length;
        public FileReadProc read;
        public FileSeekProc seek;
    }

    private delegate void FileCloseProc(IntPtr user);
    private delegate long FileLenProc(IntPtr user);
    private delegate int FileReadProc(IntPtr buffer, int length, IntPtr user);
    private delegate bool FileSeekProc(long offset, IntPtr user);

    private const int STREAMFILE_BUFFER = 1;
    private const int BASS_STREAM_STATUS = 0x800000;

    private static bool _bassPreloaded;
    private static IntPtr _bassNativeHandle;
    private static IntPtr _bassFxNativeHandle;

    // ── PeakEQ effect type and parameter struct ──
    //
    // Uses BASS_FX's PeakEQ (cross-platform pure-DSP effect).
    //
    // We define the effect type constant and parameter struct inline rather
    // than adding the ManagedBass.Fx NuGet package, because:
    //   1. ManagedBass.Fx (latest 3.0.1) may not be API-compatible with
    //      ManagedBass 4.0.2 used by this project.
    //   2. The core Bass.ChannelSetFX and Bass.FXSetParameters methods
    //      (in ManagedBass core) already support any effect type and any
    //      blittable parameter struct — no extra dependency needed.
    //   3. We only need one effect (PeakEQ), so defining it inline is
    //      simpler than pulling in a whole package.
    //
    // The native bass_fx library (bass_fx.dll / libbass_fx.so) must be
    // shipped in lib/ alongside bass.dll / libbass.so. It's preloaded
    // into the process in PreloadBassFxNative() so BASS can find it
    // when ChannelSetFX is called with the PeakEQ effect type.
    //
    // ManagedBass defines EffectType.PeakEQ (= 0x10004, matching the native
    // BASS_FX_BFX_PEAKEQ constant in bass_fx.h). We use the enum value directly.
    //
    // Must match the native BASS_BFX_PEAKEQ struct layout from bass_fx.h:
    //   typedef struct {
    //     int   lBand;        // band index (0-based)
    //     int   lChannel;     // BASS_BFX_CHANxxx flags (0 = all channels)
    //     float fCenter;      // center frequency in Hz
    //     float fGain;        // gain in dB (-30 to +30)
    //     float fBandwidth;   // bandwidth in octaves (0.1 to 4.0) [fQ can be used instead]
    //     float fQ;           // Q factor (0.1 to 10.0) [fBandwidth can be used instead]
    //   } BASS_BFX_PEAKEQ;
    // Must match the 24-byte native layout (2 ints + 4 floats). Passing less than 24 bytes
    // would result in random garbage for fQ. Setting fQ = 0 tells BASS to use fBandwidth instead.
    [StructLayout(LayoutKind.Sequential)]
    private struct PeakEqParams
    {
        public int lBand;
        public float fBandwidth;
        public float fQ;
        public float fCenter;
        public float fGain;
        public int lChannel;
    }

    // Bandwidth in octaves. The old DXParamEQ used 18 semitones (= 1.5 octaves).
    // PeakEQ's fBandwidth is directly in octaves, so 1.5f is the equivalent.
    private const float EqBandwidthOctaves = 1.5f;
    #endregion

    #region Public Properties
    public bool IsAvailable { get; private set; }
    #endregion

    #region Public Events
    public event EventHandler? PlaybackEnded;
    public event EventHandler? PlaybackStarted;
    public event EventHandler<double>? DurationChanged;
    public event EventHandler<short[]>? PcmDataAvailable;
    public event EventHandler<string>? MetadataChanged;
    #endregion

    #region Constructor
    public BassPlaybackEngine()
    {
        _dspProcedure = new DSPProcedure(OnDsp);
        _endSyncProcedure = new SyncProcedure(OnBassEndSync);
        _metaSyncProcedure = new SyncProcedure(OnBassMetaSync);
    }
    #endregion

    #region Public Methods
    public void Initialize()
    {
        var sw = Stopwatch.StartNew();
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [BASS Engine] Initializing ManagedBass...");
        try
        {
            PreloadBassNative();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // On Linux (especially under PipeWire/PulseAudio ALSA emulation), BASS default buffers
                // can cause stuttering/underruns. Increase buffer to 1000ms and update period to 50ms.
                Bass.Configure(Configuration.PlaybackBufferLength, 1000);
                Bass.Configure(Configuration.UpdatePeriod, 50);
            }
            else
            {
                // On Windows, the default BASS update period is 100ms — the DSP callback
                // (which feeds PCM to the visualizer) fires only ~10 times/sec. At 60fps,
                // this means projectM gets audio data in bursts: 5-6 frames with no new data,
                // then 1 frame with a large burst. This causes visible flickering in
                // the visualization — it reacts strongly on the burst frame,
                // then is relatively static for the next 5 frames.
                //
                // Reducing the update period to 30ms gives ~33 DSP callbacks/sec,
                // much closer to the 60fps render rate. The visualizer now gets
                // fresh audio data almost every frame, eliminating the burst
                // pattern and the resulting flicker.
                //
                // The playback buffer stays at the default 300ms — only the
                // update frequency changes. Audio playback is unaffected.
                Bass.Configure(Configuration.UpdatePeriod, 30);
            }

            bool bassOk = Bass.Init(-1, 44100, DeviceInitFlags.Default, IntPtr.Zero);
            if (bassOk || Bass.LastError == Errors.Already)
            {
                IsAvailable = true;
                _ownsBassContext = bassOk;
                Debug.WriteLine(bassOk
                    ? $"[{DateTime.Now:HH:mm:ss.fff}] [BASS Engine] ManagedBass initialized successfully in {sw.ElapsedMilliseconds}ms."
                    : $"[{DateTime.Now:HH:mm:ss.fff}] [BASS Engine] Using shared ManagedBass initialization.");

                // Load AAC, Opus, HLS, and FLAC plugins if present in the lib folder.
                //
                // basshls handles HLS (.m3u8) radio URLs, which some CDNs use
                // for ad insertion via #EXT-X-DISCONTINUITY markers.
                //
                // bassflac adds FLAC decoding — core bass.dll does not handle
                // FLAC natively (unlike MP3/OGG/WAV), so local .flac files
                // (already in Constants.AudioExtensions) fail to open via
                // Bass.CreateStream without this plugin loaded.
                //
                // Note: StreamTheWorld/Triton MediaGateway URLs are routed
                // through BASS via BassPlaybackEngine.OpenUrlStreamAsync
                // (HttpClient + BASS_StreamCreateFileUser), not MPV — see the
                // comment on that method for why BASS's own built-in HTTP
                // client can't be used directly for these streams.
                string libDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib");
                LoadPlugin(Path.Combine(libDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "basshls.dll" : "libbasshls.so"));
                LoadPlugin(Path.Combine(libDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "bass_aac.dll" : "libbass_aac.so"));
                LoadPlugin(Path.Combine(libDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "bassopus.dll" : "libbassopus.so"));
                LoadPlugin(Path.Combine(libDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "bassflac.dll" : "libbassflac.so"));
            }
            else
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [BASS Engine] ManagedBass failed to initialize. Error: {Bass.LastError}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [BASS Engine] ManagedBass Init Exception: {ex.Message}");
        }
    }

    public async Task PlayAsync(JukeboxTrack track)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("Audio playback is unavailable. ManagedBass failed to initialize.");
        }

        Stop();

        Errors error = Errors.OK;
        try
        {
            string urlToPlay = track.FilePath;

            if (urlToPlay.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                urlToPlay.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // Use HttpClient + BASS_StreamCreateFileUser for URL streams.
                //
                // BASS's built-in HTTP client (Bass.CreateStream(url, ...)) has a
                // quirk where it stops reading after ~32KB when the server sends
                // "Connection: close" — which StreamTheWorld/Triton MediaGateway
                // and some other CDNs do. HttpClient handles Connection: close
                // correctly (keeps reading until the socket actually closes).
                //
                // We open the HTTP connection, then hand the response stream to
                // BASS via BASS_StreamCreateFileUser. BASS detects the audio
                // format (AAC, MP3, Opus, HLS) and decodes through its normal
                // pipeline — EQ, visualizations, and ICY metadata all work.
                _bassStream = await OpenUrlStreamAsync(urlToPlay);
                if (_bassStream == 0)
                {
                    error = Bass.LastError;
                }
            }
            else
            {
                _bassStream = await Task.Run(() => {
                    int handle = Bass.CreateStream(urlToPlay, 0, 0, BassFlags.Default);
                    if (handle == 0) error = Bass.LastError;
                    return handle;
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BASS Engine] PlayAsync failed: {ex.Message}");
            throw new InvalidOperationException($"Could not open or resolve audio stream:\n{track.FilePath}\n\nReason: {ex.Message}");
        }

        if (_bassStream == 0)
        {
            Debug.WriteLine($"[BASS Engine] CreateStream failed for '{track.FilePath}'. Error: {error}");
            throw new InvalidOperationException($"Could not open audio stream:\n{track.FilePath}\n\nReason: {error}");
        }

        long byteLength = Bass.ChannelGetLength(_bassStream);
        double durationSeconds = Bass.ChannelBytes2Seconds(_bassStream, byteLength);
        if (double.IsNaN(durationSeconds) || double.IsInfinity(durationSeconds) || durationSeconds < 0 || durationSeconds > 315360000)
        {
            durationSeconds = 0;
        }
        DurationChanged?.Invoke(this, durationSeconds * 1000.0);

        if (track.Length == TimeSpan.Zero && durationSeconds > 0)
        {
            track.Length = TimeSpan.FromSeconds(durationSeconds);
        }

        Bass.ChannelSetAttribute(_bassStream, ChannelAttribute.Volume, _volume / 100.0);

        _dspHandle = Bass.ChannelSetDSP(_bassStream, _dspProcedure!, IntPtr.Zero, 0);
        _endSyncHandle = Bass.ChannelSetSync(_bassStream, SyncFlags.End, 0, _endSyncProcedure!, IntPtr.Zero);
        // NOTE: This sync will never fire for URL streams — those are all
        // created via BASS_StreamCreateFileUser (see OpenUrlStreamAsync),
        // which has no native ICY awareness. "Now Playing" title updates
        // for URL streams are handled manually in ReadAudioBytes/
        // ConsumeIcyMetadataBlock, which fire MetadataChanged directly.
        // This sync is only meaningful if something ever creates a stream
        // via Bass.CreateStream(url) directly again — currently nothing
        // does (the local-file branch below never passes a URL). Left in
        // place as a harmless no-op rather than removed, since it costs
        // nothing and is one less thing to re-add if that ever changes.
        _metaSyncHandle = Bass.ChannelSetSync(_bassStream, SyncFlags.MetadataReceived, 0, _metaSyncProcedure!, IntPtr.Zero);

        Bass.ChannelPlay(_bassStream);
    }

    public void Pause()
    {
        if (_bassStream != 0)
        {
            Bass.ChannelPause(_bassStream);
        }
    }

    public void Stop()
    {
        // Cancel any in-flight HTTP stream first, so OnFileRead returns
        // immediately instead of blocking on the network.
        CleanupHttpStream();

        // Reset the one-shot PlaybackStarted guard so the next PlayAsync can
        // re-fire it when fresh PCM data arrives.
        Interlocked.Exchange(ref _playbackStartedFired, 0);

        if (_bassStream != 0)
        {
            Bass.StreamFree(_bassStream);
            _bassStream = 0;
            _eqFxHandle = 0;
            _dspHandle = 0;
            _endSyncHandle = 0;
            _metaSyncHandle = 0;
        }
    }

    public void Resume()
    {
        if (_bassStream != 0)
        {
            Bass.ChannelPlay(_bassStream);
        }
    }

    public void Seek(double positionMs)
    {
        if (_bassStream != 0)
        {
            Bass.ChannelSetPosition(_bassStream, Bass.ChannelSeconds2Bytes(_bassStream, positionMs / 1000.0));
        }
    }

    public double GetPositionMs()
    {
        if (_bassStream == 0) return -1;
        var pos = Bass.ChannelGetPosition(_bassStream);
        if (pos < 0) return 0;
        double seconds = Bass.ChannelBytes2Seconds(_bassStream, pos);
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0 || seconds > 315360000)
        {
            return 0;
        }
        return seconds * 1000.0;
    }

    public void SetVolume(double volume)
    {
        _volume = volume;
        if (_bassStream != 0)
        {
            Bass.ChannelSetAttribute(_bassStream, ChannelAttribute.Volume, volume / 100.0);
        }
    }

    // Uses cross-platform BASS_FX PeakEQ instead of Windows-only DXParamEQ.
    // EQ works on Linux and macOS (requires bass_fx.dll / libbass_fx.so /
    // libbass_fx.dylib in the lib/ directory).
    public void InitializeEqBands(double[] gains, float[] centerFrequencies)
    {
        if (!IsAvailable || _bassStream == 0) return;

        // Remove any existing EQ FX before re-creating.
        if (_eqFxHandle != 0)
        {
            Bass.ChannelRemoveFX(_bassStream, _eqFxHandle);
            _eqFxHandle = 0;
        }

        _eqFxHandle = Bass.ChannelSetFX(_bassStream, EffectType.PeakEQ, 0);
        if (_eqFxHandle == 0)
        {
            Debug.WriteLine($"[BASS Engine] ChannelSetFX(PeakEQ) failed. Error: {Bass.LastError}. " +
                            "Ensure bass_fx.dll / libbass_fx.so is in the lib/ folder.");
            return;
        }
        Debug.WriteLine($"[BASS Engine] EQ FX created (handle={_eqFxHandle}).");

        // Set parameters for each band.
        for (int i = 0; i < Constants.EqBandCount; i++)
        {
            if (gains.Length > i && centerFrequencies.Length > i)
            {
                SetPeakEqParameters(i, centerFrequencies[i], (float)gains[i]);
            }
        }
    }

    // Updates a specific EQ band on all target channels.
    public void UpdateEqBand(int index, double gain, float centerFrequency)
    {
        if (!IsAvailable || _bassStream == 0 || index < 0 || index >= Constants.EqBandCount) return;

        if (_eqFxHandle == 0)
        {
            _eqFxHandle = Bass.ChannelSetFX(_bassStream, EffectType.PeakEQ, 0);
            if (_eqFxHandle == 0)
            {
                Debug.WriteLine($"[BASS Engine] UpdateEqBand: ChannelSetFX failed. Error: {Bass.LastError}.");
                return;
            }
        }

        SetPeakEqParameters(index, centerFrequency, (float)gain);
    }
    #endregion

    #region Private Methods
    /// <summary>
    /// Sets the PeakEQ parameters for a single band on the EQ FX handle.
    ///
    /// ManagedBass 4.0.2's <see cref="Bass.FXSetParameters(int, IntPtr)"/>
    /// overload takes a raw <see cref="IntPtr"/> to the parameter struct,
    /// not the struct itself (the <c>(int, IEffectParameter)</c> overload
    /// requires implementing the <c>IEffectParameter</c> interface, which
    /// is more ceremony than we need for one effect). We marshal the
    /// <see cref="PeakEqParams"/> struct to unmanaged memory, pass the
    /// pointer, then free.
    /// </summary>
    private void SetPeakEqParameters(int band, float centerFreq, float gainDb)
    {
        if (_eqFxHandle == 0) return;

        var p = new PeakEqParams
        {
            lBand = band,
            fBandwidth = EqBandwidthOctaves,
            fQ = 0.0f,
            fCenter = centerFreq,
            fGain = gainDb,
            lChannel = -1 // Apply to all channels (BASS_BFX_CHANALL)
        };

        // Use manual marshaling via IntPtr — ManagedBass's IEffectParameter
        // interface requires a FXType property we don't want to implement.
        // The IntPtr overload of FXSetParameters passes the raw struct bytes
        // directly to BASS_FXSetParameters.
        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<PeakEqParams>());
        try
        {
            Marshal.StructureToPtr(p, ptr, false);
            if (!Bass.FXSetParameters(_eqFxHandle, ptr))
            {
                Debug.WriteLine($"[BASS Engine] FXSetParameters failed for band {band} (freq={centerFreq}Hz, gain={gainDb}dB). Error: {Bass.LastError}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    // ── HttpClient URL streaming ──
    //
    // Opens an HTTP connection with .NET's HttpClient and creates a BASS
    // user stream that reads from the network. BASS detects the audio
    // format from the initial bytes and decodes through its normal pipeline.
    //
    // This replaces BASS's built-in HTTP client (Bass.CreateStream(url)),
    // which stops reading after ~32KB when the server sends Connection: close.
    private async Task<int> OpenUrlStreamAsync(string url)
    {
        // Cancel any previous stream's HTTP connection.
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = new CancellationTokenSource();
        var ct = _streamCts.Token;

        // If the caller (JukeboxViewModel) has provided an externally-cancellable
        // token (e.g. because the user started a different track while this
        // connection was still opening), link it in so cancellation propagates
        // to the HttpClient.SendAsync call below. Linked cancellation fires if
        // EITHER source fires — the engine's own _streamCts (cancelled on
        // Stop/next PlayAsync) OR the caller's external token (cancelled when
        // the VM starts a new track).
        //
        // The linked CTS is disposed in CleanupHttpStream (called by Stop and
        // by the next OpenUrlStreamAsync), keeping its lifetime aligned with
        // the stream it protects.
        _linkedStreamCts?.Dispose();
        if (_externalStreamCt.CanBeCanceled)
        {
            _linkedStreamCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _externalStreamCt);
            ct = _linkedStreamCts.Token;
        }
        else
        {
            _linkedStreamCts = null;
        }

        // Open the HTTP connection with headers that match what curl/MPV
        // send, so CDNs like StreamTheWorld serve the real live stream
        // instead of a 5-second preview/sample.
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Force HTTP/1.1 — RequestVersionExact, not RequestVersionOrLower.
        //
        // .NET's HttpClient can negotiate HTTP/2 via ALPN when the server
        // supports it. The StreamTheWorld MediaGateway server's streaming
        // model breaks under HTTP/2 — it sends a ~32KB burst and closes
        // instead of streaming continuously. curl defaults to HTTP/1.1,
        // which is why curl gets a continuous stream while HttpClient can
        // get cut off after ~32KB / ~5s.
        //
        // RequestVersionOrLower *should* prevent an upgrade to HTTP/2, but
        // it fails silently if something (ALPN, a pooled connection) hands
        // us HTTP/2 anyway — we'd see the exact same "32KB then close"
        // symptom with no way to tell why. RequestVersionExact throws
        // instead of silently negotiating, so a version mismatch becomes a
        // visible exception rather than a mystery. Combined with the
        // logging below, this either proves or kills the HTTP/2 theory.
        request.Version = new Version(1, 1);
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;

        // User-Agent: match the curl test byte-for-byte — a bare "Mozilla/5.0",
        // not a full desktop browser string.
        //
        // The previous full Chrome UA ("Mozilla/5.0 (Windows NT 10.0...)
        // Chrome/120.0.0.0 Safari/537.36") did NOT actually match what the
        // working curl test sent (curl -A "Mozilla/5.0" — literally just
        // that). Ad-stitching CDNs like Triton/StreamTheWorld commonly key
        // their routing off User-Agent: a recognizable desktop-browser UA
        // can get routed to a short "web preview" flow (the real browser
        // experience is expected to come from their own embedded player
        // hitting a different endpoint), while a bare/minimal UA looks like
        // a direct stream client (hardware receiver, app, curl) and gets the
        // continuous live stream. This is a more likely explanation for the
        // 5s cutoff than protocol version, since forcing HTTP/1.1 alone
        // (see RequestVersionExact above) didn't change anything.
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        // Accept: */* — curl sends this by default. Some servers gate on it.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        // Accept-Encoding intentionally NOT sent — curl's test didn't send
        // one either (no --compressed flag). Sending "identity" explicitly
        // is one more way this request differed from the one that's known
        // to work; dropping it makes this a closer match.
        // NOTE: Connection header intentionally NOT sent. It's a hop-by-hop
        // header, forbidden on HTTP/2 requests (RFC 7540 §8.1.2.2) and
        // redundant on HTTP/1.1 (keep-alive is already the default). There's
        // no scenario where sending it helps, and if this connection ever
        // ends up negotiating HTTP/2 despite RequestVersionExact, sending it
        // would itself be a protocol violation a compliant server could
        // legitimately reject/reset the stream for.
        // Icy-MetaData: 1 — ask the server to interleave "Now Playing"
        // StreamTitle metadata into the byte stream (classic
        // Shoutcast/Icecast/Triton ICY protocol). Without this header the
        // server sends pure audio with no title updates at all.
        //
        // BASS's own native URL client demuxes this interleaving internally
        // and fires SyncFlags.MetadataReceived. BASS_StreamCreateFileUser
        // has no idea this stream might contain interleaved metadata — it
        // just decodes whatever bytes we feed it — so we demux it ourselves
        // in ReadAudioBytes/ConsumeIcyMetadataBlock below and fire
        // MetadataChanged directly instead of relying on OnBassMetaSync.
        request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");

        try
        {
            _httpResponse = await _httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, ct);
            _httpResponse.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BASS Engine] HttpClient request failed for '{url}': {ex.Message}");
            throw new InvalidOperationException($"Could not open radio stream:\n{url}\n\nReason: {ex.Message}");
        }

        // "icy-metaint: N" in the response means the server agreed to
        // interleave a metadata block every N bytes of audio. Absence means
        // this station doesn't support ICY metadata (or we're not talking
        // to a Shoutcast/Icecast-style server) — ReadAudioBytes falls back
        // to a plain passthrough read in that case.
        _icyMetaInt = 0;
        if (_httpResponse.Headers.TryGetValues("icy-metaint", out var metaIntValues) &&
            int.TryParse(metaIntValues.FirstOrDefault(), out int metaInt) && metaInt > 0)
        {
            _icyMetaInt = metaInt;
        }
        _icyBytesUntilMeta = _icyMetaInt;
        Debug.WriteLine(_icyMetaInt > 0
            ? $"[BASS Engine] ICY metadata enabled, interval={_icyMetaInt} bytes."
            : "[BASS Engine] ICY metadata not offered by server (no icy-metaint header).");

        // Diagnostics: confirm what actually happened on the wire.
        //
        // _httpResponse.Version tells us the negotiated protocol directly —
        // this is the one log line that resolves the HTTP/2-vs-preview
        // ambiguity. RequestMessage.RequestUri shows where the 302 redirect
        // actually landed (the real CDN edge host), and Set-Cookie shows
        // whether the server issued session cookies on this response chain
        // at all — useful for confirming/killing the cookie theory below.
        var finalUri = _httpResponse.RequestMessage?.RequestUri;
        var setCookie = _httpResponse.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? string.Join(" | ", cookies) : "(none)";
        Debug.WriteLine($"[BASS Engine] Negotiated protocol: HTTP/{_httpResponse.Version}. " +
                        $"Final URI after redirects: {finalUri}. Set-Cookie: {setCookie}.");

        // Log the full response headers so we can see exactly what the
        // server sent — useful for diagnosing preview-vs-stream issues.
        // Key headers to look for:
        //   - Content-Type: should be audio/aacp for AAC+
        //   - Content-Length: null for a live stream, a number for a preview
        //   - Connection: close is normal for StreamTheWorld (they stream
        //     continuously then close, unlike keep-alive Shoutcast)
        //   - icy-br: bitrate (48 = 48kbps HE-AAC)
        var respHeaders = string.Join(", ", _httpResponse.Headers.Select(h => $"{h.Key}={string.Join(",", h.Value)}"));
        var contentHeaders = string.Join(", ", _httpResponse.Content.Headers.Select(h => $"{h.Key}={string.Join(",", h.Value)}"));
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [BASS Engine] HttpClient connected to '{url}'.");
        Debug.WriteLine($"[BASS Engine]   Response headers: {respHeaders}");
        Debug.WriteLine($"[BASS Engine]   Content headers: {contentHeaders}");

        _networkStream = await _httpResponse.Content.ReadAsStreamAsync(ct);
        _readBuffer = new byte[16384]; // 16KB read buffer
        _streamStartTimestamp = DateTime.UtcNow;
        _streamBytesReceived = 0;

        // Set up the BASS file callback delegates. These MUST be assigned to
        // fields (not locals) so the GC keeps them alive — BASS holds a raw
        // pointer to the BASS_FILEPROCS struct, which contains raw function
        // pointers to these delegates. If the delegates are collected, BASS
        // will crash when it tries to call them.
        _fileCloseProc = new FileCloseProc(OnFileClose);
        _fileLenProc = new FileLenProc(OnFileLength);
        _fileReadProc = new FileReadProc(OnFileRead);
        _fileSeekProc = new FileSeekProc(OnFileSeek);

        _fileProcs = new BASS_FILEPROCS
        {
            close = _fileCloseProc,
            length = _fileLenProc,
            read = _fileReadProc,
            seek = _fileSeekProc,
        };

        // Create the BASS user stream.
        //
        // STREAMFILE_BUFFER (1): BASS pre-buffers in a background thread,
        //   matching how Bass.CreateStream(url) works internally. BASS calls
        //   our read callback to fill its buffer, then decodes from the buffer.
        //
        // BASS_STREAM_STATUS (0x800000): allows BASS to report download
        //   progress, which we don't use but is harmless.
        //
        // BASS does format detection from the first bytes read — it will try
        // basshls (if the response is an m3u8 playlist), bass_aac (if AAC),
        // bassopus (if Opus), or Media Foundation (Windows built-in) in that
        // order. This happens automatically inside BASS_StreamCreateFileUser.
        int handle = BASS_StreamCreateFileUser(
            STREAMFILE_BUFFER,
            BASS_STREAM_STATUS,
            ref _fileProcs,
            IntPtr.Zero);

        if (handle == 0)
        {
            Debug.WriteLine($"[BASS Engine] BASS_StreamCreateFileUser failed. Error: {Bass.LastError}");
            CleanupHttpStream();
        }
        else
        {
            // Log which decoder BASS chose.
            try
            {
                var info = Bass.ChannelGetInfo(handle);
                Debug.WriteLine($"[BASS Engine] URL stream created: handle={handle}, " +
                                $"codec={info.ChannelType}, freq={info.Frequency}, " +
                                $"chans={info.Channels}, plugin={info.Plugin}.");
            }
            catch { }
        }

        return handle;
    }

    // BASS file callback: return the total length of the stream.
    // For a live radio stream, the length is unknown — return 0.
    // BASS will treat the stream as "length unknown" and won't allow seeking,
    // which is correct for live radio.
    private long OnFileLength(IntPtr user)
    {
        return 0;
    }

    // BASS file callback: read data from the network stream into BASS's buffer.
    //
    // This is called on BASS's internal buffer thread (not the audio thread).
    // It blocks until data is available, then returns what we've got. For a
    // live stream, the HttpClient stream blocks in Read() until the server
    // sends more data — this is the desired behavior, it's how BASS knows to
    // wait for more audio.
    //
    // Returns the number of bytes actually read. Returning 0 signals EOF,
    // which fires the End sync. Returning -1 signals an error.
    private int OnFileRead(IntPtr buffer, int length, IntPtr user)
    {
        if (_networkStream == null || _readBuffer == null) return 0;

        int totalRead = 0;
        while (totalRead < length)
        {
            int read;
            try
            {
                // ReadAudioBytes strips out and parses any interleaved ICY
                // metadata block transparently — BASS only ever sees pure
                // audio bytes here, same as before ICY support was added.
                read = ReadAudioBytes(buffer, totalRead, length - totalRead);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BASS Engine] OnFileRead stream read failed: {ex.Message}");
                return totalRead > 0 ? totalRead : 0;
            }

            if (read == 0)
            {
                // End of stream — server closed the connection.
                var elapsed = DateTime.UtcNow - _streamStartTimestamp;
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [BASS Engine] Network stream ended (server closed connection). " +
                                $"Received {_streamBytesReceived:N0} bytes in {elapsed.TotalSeconds:F1}s. " +
                                $"At 48kbps, that's ~{_streamBytesReceived / 6000:F1}s of audio.");
                break;
            }

            totalRead += read;

            // Log every 50KB so we can see if data is arriving continuously
            // (good — streaming) or in one burst then stopping (bad — preview).
            if (_streamBytesReceived % 51200 < 16384)
            {
                var elapsed = DateTime.UtcNow - _streamStartTimestamp;
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [BASS Engine] Stream progress: {_streamBytesReceived:N0} bytes in {elapsed.TotalSeconds:F1}s.");
            }
        }

        return totalRead;
    }

    // Reads up to `count` bytes of pure AUDIO data from the network into
    // `buffer` at `offset`. If the server is interleaving ICY metadata
    // (_icyMetaInt > 0), this caps each network read at the next metadata
    // boundary and, on hitting it, consumes and parses the metadata block
    // before continuing — the caller (OnFileRead) never sees those bytes.
    //
    // Same return contract as Stream.Read: bytes of audio written (may be
    // less than `count`, same call may need to be repeated), 0 on EOF.
    // Exceptions propagate to the caller, same as before ICY support.
    private int ReadAudioBytes(IntPtr buffer, int offset, int count)
    {
        if (_icyMetaInt > 0 && _icyBytesUntilMeta <= 0)
        {
            ConsumeIcyMetadataBlock();
        }

        int toRead = _icyMetaInt > 0 ? Math.Min(count, _icyBytesUntilMeta) : count;
        toRead = Math.Min(toRead, _readBuffer!.Length);

        int read = _networkStream!.Read(_readBuffer, 0, toRead);
        if (read <= 0) return 0;

        _streamBytesReceived += read;
        Marshal.Copy(_readBuffer, 0, buffer + offset, read);

        if (_icyMetaInt > 0)
        {
            _icyBytesUntilMeta -= read;
        }

        return read;
    }

    // Reads and parses one ICY metadata block from the network stream:
    //   [1 length byte L] [L*16 bytes of metadata text, null-padded]
    // L=0 means "no change since the last block" — the length byte is
    // still sent and must still be consumed, but there's no text to read.
    // Reuses ParseStreamTitle (originally written for BASS's native
    // TagType.META format) since the "StreamTitle='...';" text format is
    // identical either way.
    private void ConsumeIcyMetadataBlock()
    {
        int lengthByte = _networkStream!.ReadByte();
        if (lengthByte < 0)
        {
            // Stream ended while expecting a metadata block. Leave the
            // counter at zero so the next audio read is attempted and
            // discovers EOF the normal way.
            _icyBytesUntilMeta = _icyMetaInt;
            return;
        }

        int metaLength = lengthByte * 16;
        if (metaLength > 0)
        {
            var metaBytes = new byte[metaLength];
            int metaRead = 0;
            while (metaRead < metaLength)
            {
                int n = _networkStream.Read(metaBytes, metaRead, metaLength - metaRead);
                if (n <= 0) break; // stream ended mid-metadata block
                metaRead += n;
            }

            // ICY metadata is classically Latin-1; Latin1 can decode any
            // byte value without throwing, unlike strict ASCII.
            string metadata = System.Text.Encoding.Latin1.GetString(metaBytes, 0, metaRead).TrimEnd('\0');
            string? title = ParseStreamTitle(metadata);
            if (!string.IsNullOrWhiteSpace(title))
            {
                MetadataChanged?.Invoke(this, title);
            }
        }

        _icyBytesUntilMeta = _icyMetaInt;
    }

    // BASS file callback: seek to a position in the stream.
    // For a live radio stream, seeking is not possible — return false.
    private bool OnFileSeek(long offset, IntPtr user)
    {
        return false; // cannot seek a live stream
    }

    // BASS file callback: close the stream.
    // Called by BASS when the stream is freed (Bass.StreamFree).
    private void OnFileClose(IntPtr user)
    {
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [BASS Engine] OnFileClose — closing network stream.");
        CleanupHttpStream();
    }

    // Cleans up the HttpClient response and network stream.
    private void CleanupHttpStream()
    {
        _streamCts?.Cancel();
        if (_networkStream != null)
        {
            try { _networkStream.Dispose(); } catch { }
            _networkStream = null;
        }
        if (_httpResponse != null)
        {
            try { _httpResponse.Dispose(); } catch { }
            _httpResponse = null;
        }
        // Dispose the linked CTS (created in OpenUrlStreamAsync when an
        // external token is in use). Not strictly required for correctness
        // — the underlying tokens live elsewhere — but disposing avoids
        // accumulating unmanaged timer registrations across many stream
        // open/close cycles.
        if (_linkedStreamCts != null)
        {
            try { _linkedStreamCts.Dispose(); } catch { }
            _linkedStreamCts = null;
        }
    }

    private static void PreloadBassNative()
    {
        if (_bassPreloaded) return;
        _bassPreloaded = true;

        // Register a DllImportResolver on the ManagedBass assembly so its
        // [DllImport("bass")] calls resolve to our lib/ folder. On Windows
        // this is redundant (NativeLibrary.Load is process-global), but on
        // Linux .NET scopes native handles per-assembly — so a library
        // loaded by the Jukebox assembly is invisible to ManagedBass.dll's
        // P/Invoke. The resolver makes it work on both platforms.
        NativeLibrary.SetDllImportResolver(typeof(Bass).Assembly, (name, assembly, path) =>
        {
            string libDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib");

            if (name == "bass")
            {
                if (_bassNativeHandle != IntPtr.Zero)
                    return _bassNativeHandle;

                string fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? "bass.dll"
                    : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                        ? "libbass.so"
                        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                            ? "libbass.dylib"
                            : "bass";

                string fullPath = Path.Combine(libDir, fileName);
                if (File.Exists(fullPath))
                {
                    if (NativeLibrary.TryLoad(fullPath, out _bassNativeHandle))
                    {
                        Debug.WriteLine($"[BASS Engine] Loaded BASS native library from: {fullPath}");
                        return _bassNativeHandle;
                    }
                }

                if (NativeLibrary.TryLoad(fileName, out _bassNativeHandle))
                {
                    Debug.WriteLine($"[BASS Engine] Loaded BASS native library from OS search path: {fileName}");
                    return _bassNativeHandle;
                }

                Debug.WriteLine($"[BASS Engine] BASS native library not found. Looked in: {fullPath} and OS default search path.");
            }

            if (name == "bass_fx")
            {
                if (_bassFxNativeHandle != IntPtr.Zero)
                    return _bassFxNativeHandle;
                string fxFile = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? "bass_fx.dll"
                    : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                        ? "libbass_fx.so"
                        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                            ? "libbass_fx.dylib"
                            : "bass_fx";
                string fxFullPath = Path.Combine(libDir, fxFile);
                if (File.Exists(fxFullPath) && NativeLibrary.TryLoad(fxFullPath, out _bassFxNativeHandle))
                {
                    Debug.WriteLine($"[BASS Engine] Loaded BASS_FX from: {fxFullPath}");
                    return _bassFxNativeHandle;
                }
                if (NativeLibrary.TryLoad(fxFile, out _bassFxNativeHandle))
                {
                    Debug.WriteLine($"[BASS Engine] Loaded BASS_FX from OS search path: {fxFile}");
                    return _bassFxNativeHandle;
                }
                Debug.WriteLine($"[BASS Engine] BASS_FX NOT FOUND. EQ will not work. Looked in: {fxFullPath}");
            }

            return IntPtr.Zero;
        });

        // Preload bass_fx into the process so BASS can find it when
        // ChannelSetFX is called with the PeakEQ effect type. BASS loads
        // bass_fx dynamically via LoadLibrary/dlopen — if we preload it
        // here, BASS finds it already in the process address space.
        // Without this, bass_fx must be on the OS default search path
        // (which it isn't in our flat lib/ layout).
        PreloadBassFxNative();
    }

    private static void PreloadBassFxNative()
    {
        if (_bassFxNativeHandle != IntPtr.Zero) return;

        string libDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib");
        string fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "bass_fx.dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? "libbass_fx.so"
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? "libbass_fx.dylib"
                    : "bass_fx";

        // 1) Try the lib/ drop-in folder.
        string fullPath = Path.Combine(libDir, fileName);
        if (File.Exists(fullPath))
        {
            if (NativeLibrary.TryLoad(fullPath, out _bassFxNativeHandle))
            {
                Debug.WriteLine($"[BASS Engine] Loaded BASS_FX native library from: {fullPath}");
                return;
            }
        }

        // 2) Fall back to OS default search path.
        if (NativeLibrary.TryLoad(fileName, out _bassFxNativeHandle))
        {
            Debug.WriteLine($"[BASS Engine] Loaded BASS_FX native library from OS search path: {fileName}");
            return;
        }

        Debug.WriteLine($"[BASS Engine] BASS_FX native library not found. Looked in: {fullPath} and OS default search path. " +
                        "EQ will not be available. Download from https://www.un4seen.com/ (bass_fx add-on).");
    }

    // Use the standard event-invocation pattern with a local copy to prevent
    // NullReferenceException during shutdown.
    //
    // OnDsp runs on BASS's internal audio thread. The dispose path on the
    // UI thread nulls PcmDataAvailable, then calls Bass.StreamFree. Without
    // the local copy, the following race can occur:
    //   1. BASS thread: reads PcmDataAvailable (non-null), passes the check
    //   2. UI thread:   sets PcmDataAvailable = null
    //   3. UI thread:   calls Bass.StreamFree (blocks waiting for BASS thread)
    //   4. BASS thread: reads PcmDataAvailable again for Invoke — now null → NRE
    //
    // A NRE on BASS's audio thread is unrecoverable — it tears down the
    // process. The local-copy pattern captures the delegate reference
    // atomically at entry, so even if the field is nulled mid-call, the
    // local copy is still valid.
    private void OnDsp(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        if (length <= 0) return;

        // First PCM buffer => raise PlaybackStarted (one-shot per stream).
        // Interlocked.CompareExchange is the thread-safe equivalent of
        // "if (!_playbackStartedFired) { _playbackStartedFired = true; raise; }".
        if (Interlocked.CompareExchange(ref _playbackStartedFired, 1, 0) == 0)
        {
            PlaybackStarted?.Invoke(this, EventArgs.Empty);
        }

        var handler = PcmDataAvailable;
        if (handler == null) return;

        int count = length / 2;
        short[] pcm = new short[count];
        Marshal.Copy(buffer, pcm, 0, count);
        handler.Invoke(this, pcm);
    }

    private void OnBassEndSync(int handle, int channel, int data, IntPtr user)
    {
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    private void OnBassMetaSync(int handle, int channel, int data, IntPtr user)
    {
        try
        {
            IntPtr ptr = Bass.ChannelGetTags(channel, TagType.META);
            if (ptr != IntPtr.Zero)
            {
                string? metadata = Marshal.PtrToStringAnsi(ptr);
                string? title = ParseStreamTitle(metadata);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    MetadataChanged?.Invoke(this, title);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BASS Engine] OnBassMetaSync failed: {ex.Message}");
        }
    }

    private static string? ParseStreamTitle(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return null;

        int titleIdx = metadata.IndexOf("StreamTitle='", StringComparison.OrdinalIgnoreCase);
        if (titleIdx < 0) return null;

        int start = titleIdx + "StreamTitle='".Length;
        int end = metadata.IndexOf("';", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            end = metadata.IndexOf("'", start, StringComparison.OrdinalIgnoreCase);
        }

        if (end > start)
        {
            return metadata.Substring(start, end - start).Trim();
        }

        return null;
    }

    private void LoadPlugin(string path)
    {
        if (File.Exists(path))
        {
            // Preload the native library into the process first so that the unmanaged loader can find it and resolve its static dependencies
            if (NativeLibrary.TryLoad(path, out IntPtr handle))
            {
                Debug.WriteLine($"[BASS Engine] Preloaded plugin successfully via NativeLibrary: {path}");
            }

            int pluginHandle = Bass.PluginLoad(path);
            if (pluginHandle != 0)
            {
                Debug.WriteLine($"[BASS Engine] Loaded plugin successfully: {path}");
            }
            else
            {
                Debug.WriteLine($"[BASS Engine] Failed to load plugin: {path}. Error: {Bass.LastError}");
            }
        }
    }
    #endregion

    #region Dispose
    public void Dispose()
    {
        // Detach listeners first so any in-flight DSP callback sees null
        // and skips the Invoke (see OnDsp local-copy pattern).
        PcmDataAvailable = null;

        if (_bassStream != 0)
        {
            // Explicitly remove DSP and SYNC callbacks before freeing the
            // stream. Bass.StreamFree would auto-remove them, but explicit
            // removal guarantees any in-flight callback has been drained
            // before the stream memory is released. This is the belt-and-
            // suspenders companion to the OnDsp local-copy fix.
            if (_dspHandle != 0)
            {
                try { Bass.ChannelRemoveDSP(_bassStream, _dspHandle); }
                catch (Exception ex) { Debug.WriteLine($"[BASS Engine] ChannelRemoveDSP failed: {ex.Message}"); }
                _dspHandle = 0;
            }
            if (_endSyncHandle != 0)
            {
                try { Bass.ChannelRemoveSync(_bassStream, _endSyncHandle); }
                catch (Exception ex) { Debug.WriteLine($"[BASS Engine] ChannelRemoveSync failed: {ex.Message}"); }
                _endSyncHandle = 0;
            }
            if (_metaSyncHandle != 0)
            {
                try { Bass.ChannelRemoveSync(_bassStream, _metaSyncHandle); }
                catch (Exception ex) { Debug.WriteLine($"[BASS Engine] ChannelRemoveSync (meta) failed: {ex.Message}"); }
                _metaSyncHandle = 0;
            }

            try { Bass.StreamFree(_bassStream); }
            catch (Exception ex) { Debug.WriteLine($"[BASS Engine] StreamFree failed: {ex.Message}"); }
            _bassStream = 0;
            _eqFxHandle = 0;
        }

        if (IsAvailable && _ownsBassContext)
        {
            Bass.Free();
        }

        IsAvailable = false;
    }
    #endregion
}
