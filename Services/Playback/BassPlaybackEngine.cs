using Jukebox.Models;
using Jukebox.Native;
using System;
using System.Collections.Generic;
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

    private BassNative.BassDspProcedure? _dspProcedure;
    private BassNative.BassSyncProcedure? _endSyncProcedure;
    private int _dspHandle;
    private int _endSyncHandle;
    private bool _ownsBassContext;
    private double _volume = 100;
    private BassNative.BassSyncProcedure? _metaSyncProcedure;
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

    // ── Per-host User-Agent overrides ──
    //
    // The default UA (DefaultBrowserUserAgent) is a full desktop Chrome string
    // that satisfies Zeno.Fm, SurferNetwork, and standard Icecast/Shoutcast
    // stations. A small set of ad-stitching CDNs (currently just Triton /
    // StreamTheWorld) route browser UAs to a 5-second preview and need a bare
    // "Mozilla/5.0" instead.
    //
    // Entries are hostname SUFFIXES — a request to
    // "27263.live.streamtheworld.com" matches the "streamtheworld.com" entry.
    // The check is case-insensitive and anchored at the host boundary.
    //
    // To add a new override, append an entry here. If a future CDN exhibits
    // the "5-second preview" pattern (connection closes after exactly 32 KB),
    // add its host suffix to this map with the bare UA.
    private const string DefaultBrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private const string BareUserAgent = "Mozilla/5.0";

    private static readonly Dictionary<string, string> _hostUserAgentOverrides =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Triton / StreamTheWorld MediaGateway — serves a 5-second preview
            // (exactly 32768 bytes) to browser UAs. Bare UA gets the continuous
            // live stream. Covers all *.streamtheworld.com subdomains including
            // the per-station shards like "27263.live.streamtheworld.com".
            { "streamtheworld.com", BareUserAgent },
        };

    /// <summary>
    /// Returns the User-Agent string to send for a request to the given URI.
    /// Walks the host's dot-suffix chain and returns the first matching
    /// override; falls back to <see cref="DefaultBrowserUserAgent"/> if no
    /// override applies.
    /// </summary>
    private static string GetUserAgentForHost(Uri? uri)
    {
        if (uri != null && !string.IsNullOrEmpty(uri.Host))
        {
            string host = uri.Host;
            // Walk the suffix chain: "27263.live.streamtheworld.com" →
            // "live.streamtheworld.com" → "streamtheworld.com" → "com".
            // Stop before the TLD-only fragment to avoid silly matches.
            string[] parts = host.Split('.');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string suffix = string.Join('.', parts, i, parts.Length - i);
                if (_hostUserAgentOverrides.TryGetValue(suffix, out var ua))
                {
                    return ua;
                }
            }
        }
        return DefaultBrowserUserAgent;
    }
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
    private BassNative.BASS_FILEPROCS _fileProcs;
    private BassNative.BassFileProcClose? _fileCloseProc;
    private BassNative.BassFileProcLength? _fileLenProc;
    private BassNative.BassFileProcRead? _fileReadProc;
    private BassNative.BassFileProcSeek? _fileSeekProc;

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
        _dspProcedure = new BassNative.BassDspProcedure(OnDsp);
        _endSyncProcedure = new BassNative.BassSyncProcedure(OnBassEndSync);
        _metaSyncProcedure = new BassNative.BassSyncProcedure(OnBassMetaSync);
    }
    #endregion

    #region Public Methods
    public void Initialize()
    {
        var sw = Stopwatch.StartNew();
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [BASS Engine] Initializing BASS...");
        try
        {
            BassNative.EnsureLoaded();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // On Linux (especially under PipeWire/PulseAudio ALSA emulation), BASS default buffers
                // can cause stuttering/underruns. Increase buffer to 1000ms and update period to 50ms.
                BassNative.Configure(BassNative.BassConfiguration.PlaybackBufferLength, 1000);
                BassNative.Configure(BassNative.BassConfiguration.UpdatePeriod, 50);
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
                BassNative.Configure(BassNative.BassConfiguration.UpdatePeriod, 30);
            }

            // BASS_Init: device = -1 → default output device.
            // All 5 parameters must be passed; omitting clsid shifts the device
            // argument and silently selects the silent device 0.
            bool bassOk = BassNative.Init(-1, 44100, BassNative.BassInitFlags.Default, IntPtr.Zero, IntPtr.Zero);
            if (bassOk || BassNative.GetLastError() == BassNative.BassErrors.Already)
            {
                IsAvailable = true;
                _ownsBassContext = bassOk;
                Debug.WriteLine(bassOk
                    ? $"[{DateTime.Now:HH:mm:ss.fff}] [BASS Engine] BASS initialized successfully in {sw.ElapsedMilliseconds}ms."
                    : $"[{DateTime.Now:HH:mm:ss.fff}] [BASS Engine] Using shared BASS initialization.");

                // Load AAC, Opus, HLS, and FLAC plugins if present in the lib folder.
                //
                // basshls handles HLS (.m3u8) radio URLs, which some CDNs use
                // for ad insertion via #EXT-X-DISCONTINUITY markers.
                //
                // bassflac adds FLAC decoding — core bass.dll does not handle
                // FLAC natively (unlike MP3/OGG/WAV), so local .flac files
                // (already in Constants.AudioExtensions) fail to open via
                // BassNative.CreateStream without this plugin loaded.
                //
                // Note: StreamTheWorld/Triton MediaGateway URLs are routed
                // through BASS via BassPlaybackEngine.OpenUrlStreamAsync
                // (HttpClient + BASS_StreamCreateFileUser), not MPV — see the
                // comment on that method for why BASS's own built-in HTTP
                // client can't be used directly for these streams.
                string libDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib");
                BassNative.LoadPlugin(Path.Combine(libDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "basshls.dll" : "libbasshls.so"));
                BassNative.LoadPlugin(Path.Combine(libDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "bass_aac.dll" : "libbass_aac.so"));
                BassNative.LoadPlugin(Path.Combine(libDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "bassopus.dll" : "libbassopus.so"));
                BassNative.LoadPlugin(Path.Combine(libDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "bassflac.dll" : "libbassflac.so"));
            }
            else
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [BASS Engine] BASS failed to initialize. Error: {BassNative.GetLastError()}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [BASS Engine] BASS Init Exception: {ex.Message}");
        }
    }

    public async Task PlayAsync(JukeboxTrack track)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("Audio playback is unavailable. BASS failed to initialize.");
        }

        Stop();

        BassNative.BassErrors error = BassNative.BassErrors.OK;
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
                    error = BassNative.GetLastError();
                }
            }
            else
            {
                _bassStream = await Task.Run(() => {
                    int handle = BassNative.CreateStream(urlToPlay, BassNative.BassFlags.Default);
                    if (handle == 0) error = BassNative.GetLastError();
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

        long byteLength = BassNative.ChannelGetLength(_bassStream);
        double durationSeconds = BassNative.ChannelBytes2Seconds(_bassStream, byteLength);
        if (double.IsNaN(durationSeconds) || double.IsInfinity(durationSeconds) || durationSeconds < 0 || durationSeconds > 315360000)
        {
            durationSeconds = 0;
        }
        DurationChanged?.Invoke(this, durationSeconds * 1000.0);

        if (track.Length == TimeSpan.Zero && durationSeconds > 0)
        {
            track.Length = TimeSpan.FromSeconds(durationSeconds);
        }

        BassNative.ChannelSetAttribute(_bassStream, BassNative.BassChannelAttribute.Volume, _volume / 100.0);

        _dspHandle = BassNative.ChannelSetDSP(_bassStream, _dspProcedure!, IntPtr.Zero, 0);
        _endSyncHandle = BassNative.ChannelSetSync(_bassStream, BassNative.BassSyncFlags.End, 0, _endSyncProcedure!, IntPtr.Zero);
        // NOTE: This sync will never fire for URL streams — those are all
        // created via BASS_StreamCreateFileUser (see OpenUrlStreamAsync),
        // which has no native ICY awareness. "Now Playing" title updates
        // for URL streams are handled manually in ReadAudioBytes/
        // ConsumeIcyMetadataBlock, which fire MetadataChanged directly.
        // This sync is only meaningful if something ever creates a stream
        // via BassNative.CreateStream directly again — currently nothing
        // does (the local-file branch below never passes a URL). Left in
        // place as a harmless no-op rather than removed, since it costs
        // nothing and is one less thing to re-add if that ever changes.
        _metaSyncHandle = BassNative.ChannelSetSync(_bassStream, BassNative.BassSyncFlags.MetadataReceived, 0, _metaSyncProcedure!, IntPtr.Zero);

        BassNative.ChannelPlay(_bassStream);
    }

    public void Pause()
    {
        if (_bassStream != 0)
        {
            BassNative.ChannelPause(_bassStream);
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
            BassNative.StreamFree(_bassStream);
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
            BassNative.ChannelPlay(_bassStream);
        }
    }

    public void Seek(double positionMs)
    {
        if (_bassStream != 0)
        {
            BassNative.ChannelSetPosition(_bassStream, BassNative.ChannelSeconds2Bytes(_bassStream, positionMs / 1000.0));
        }
    }

    public double GetPositionMs()
    {
        if (_bassStream == 0) return -1;
        var pos = BassNative.ChannelGetPosition(_bassStream);
        if (pos < 0) return 0;
        double seconds = BassNative.ChannelBytes2Seconds(_bassStream, pos);
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
            BassNative.ChannelSetAttribute(_bassStream, BassNative.BassChannelAttribute.Volume, volume / 100.0);
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
            BassNative.ChannelRemoveFX(_bassStream, _eqFxHandle);
            _eqFxHandle = 0;
        }

        _eqFxHandle = BassNative.ChannelSetFX(_bassStream, BassNative.BassEffectType.PeakEQ, 0);
        if (_eqFxHandle == 0)
        {
            Debug.WriteLine($"[BASS Engine] ChannelSetFX(PeakEQ) failed. Error: {BassNative.GetLastError()}. " +
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
            _eqFxHandle = BassNative.ChannelSetFX(_bassStream, BassNative.BassEffectType.PeakEQ, 0);
            if (_eqFxHandle == 0)
            {
                Debug.WriteLine($"[BASS Engine] UpdateEqBand: ChannelSetFX failed. Error: {BassNative.GetLastError()}.");
                return;
            }
        }

        SetPeakEqParameters(index, centerFrequency, (float)gain);
    }
    #endregion

    #region Private Methods
    // Sets the PeakEQ parameters for a single band on the EQ FX handle.
    // Marshals the BassNative.PeakEqParams struct to unmanaged memory,
    // passes the raw IntPtr to BASS_FXSetParameters, then frees.
    private void SetPeakEqParameters(int band, float centerFreq, float gainDb)
    {
        if (_eqFxHandle == 0) return;

        var p = new BassNative.PeakEqParams
        {
            lBand = band,
            fBandwidth = EqBandwidthOctaves,
            fQ = 0.0f,
            fCenter = centerFreq,
            fGain = gainDb,
            lChannel = -1 // Apply to all channels (BASS_BFX_CHANALL)
        };

        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<BassNative.PeakEqParams>());
        try
        {
            Marshal.StructureToPtr(p, ptr, false);
            if (!BassNative.FXSetParameters(_eqFxHandle, ptr))
            {
                Debug.WriteLine($"[BASS Engine] FXSetParameters failed for band {band} (freq={centerFreq}Hz, gain={gainDb}dB). Error: {BassNative.GetLastError()}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    // Detects whether a URL is an HLS playlist (.m3u8 or .m3u extension).
    // Used to route the stream through BASS's native URL client instead of
    // the HttpClient + StreamCreateFileUser path. basshls intercepts the URL
    // and handles playlist parsing + segment fetching internally.
    private static bool IsHlsUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        try
        {
            // Ignore query string when checking extension
            string path = new Uri(url).AbsolutePath;
            return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Not a valid absolute URI — fall back to a simple substring check
            // (covers edge cases like malformed URLs from radio-browser cache)
            int q = url.IndexOf('?');
            string pathOnly = q >= 0 ? url.Substring(0, q) : url;
            return pathOnly.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                   pathOnly.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase);
        }
    }

    // Opens an HLS stream via BASS's native URL streaming client. basshls
    // (loaded as a plugin) intercepts the URL and handles:
    //   1. Downloading the .m3u8 playlist
    //   2. Parsing variant streams (master playlist → media playlist)
    //   3. Fetching each .ts/.aac segment
    //   4. Decoding and gaplessly stitching segments together
    //
    // This can't go through the HttpClient path because BASS needs to make
    // its own HTTP requests for each segment — HttpClient only gives us the
    // playlist bytes, which aren't audio.
    //
    // Uses BASS_StreamCreateURL (via BassNative.CreateUrlStream) — the
    // documented BASS function for internet URLs. BASS_StreamCreateFile does
    // NOT accept URLs and returns BASS_ERROR_ILLPARAM (20) if you try.
    //
    // The User-Agent is set via BASS_SetConfigPtr right before the call.
    // BASS reads the config at the moment it makes the HTTP request, so the
    // per-host UA override (GetUserAgentForHost) applies correctly.
    //
    // BASS_StreamCreateURL is synchronous and blocks until the playlist is
    // fetched and at least the first segment is buffered. We run it on a
    // background thread (Task.Run) to avoid blocking the UI thread.
    private async Task<int> OpenHlsStreamAsync(string url)
    {
        // Set the User-Agent for BASS's HTTP requests (playlist + segment fetches).
        // The per-host override applies — e.g. streamtheworld.com HLS gets the
        // bare UA, zeno.fm HLS gets the full Chrome UA.
        Uri? uri;
        try { uri = new Uri(url); }
        catch { uri = null; }

        string userAgent = GetUserAgentForHost(uri);
        BassNative.SetConfigPtr(BassNative.BassConfiguration.NetAgent, userAgent);

        // 15-second timeout for HLS operations — covers playlist fetch + initial
        // segment buffering. The default BASS timeout is 0 (no timeout), which
        // would hang the UI indefinitely if the server is unresponsive.
        BassNative.Configure(BassNative.BassConfiguration.NetTimeout, 15);

        Debug.WriteLine($"[BASS Engine] HLS stream — using BASS native URL client. UA='{userAgent}', URL='{url}'.");

        // ── Pre-flight HTTP check ──
        //
        // BASS_StreamCreateURL returns generic BASS error codes that don't
        // distinguish between "server returned 403" and "invalid parameter".
        // Probe the URL with HttpClient first so we can surface the actual
        // HTTP status (403, 404, 451 geo-block, etc.) — much more useful for
        // the user than "BASS error 20".
        //
        // We use HttpCompletionOption.ResponseHeadersRead so we don't download
        // the whole playlist — just enough to see the status code and headers.
        try
        {
            using var preflightReq = new HttpRequestMessage(HttpMethod.Get, url);
            preflightReq.Headers.UserAgent.ParseAdd(userAgent);
            preflightReq.Headers.Accept.ParseAdd("*/*");
            preflightReq.Version = new Version(1, 1);
            preflightReq.VersionPolicy = HttpVersionPolicy.RequestVersionExact;

            using var preflightResp = await _httpClient.SendAsync(preflightReq,
                HttpCompletionOption.ResponseHeadersRead);
            if (!preflightResp.IsSuccessStatusCode)
            {
                int code = (int)preflightResp.StatusCode;
                string reason = string.IsNullOrWhiteSpace(preflightResp.ReasonPhrase)
                    ? "" : $" ({preflightResp.ReasonPhrase})";
                Debug.WriteLine($"[BASS Engine] HLS pre-flight failed: HTTP {code}{reason} for '{url}'");
                throw new InvalidOperationException(
                    $"Could not open HLS stream:\n{url}\n\n" +
                    $"Reason: server returned HTTP {code}{reason}.");
            }
            Debug.WriteLine($"[BASS Engine] HLS pre-flight OK — HTTP {preflightResp.StatusCode}.");
        }
        catch (InvalidOperationException) { throw; }  // re-throw the friendly HTTP error
        catch (Exception ex)
        {
            Debug.WriteLine($"[BASS Engine] HLS pre-flight exception: {ex.Message}");
            // Don't throw — let BASS try anyway, maybe it has better luck
            // (e.g. the pre-flight failed for a transient reason but BASS will retry)
        }

        // BASS_StreamCreateURL with a URL is synchronous — run on a background
        // thread to avoid blocking the UI. The function blocks until the
        // playlist is fetched and the first segment is buffered.
        //
        // Uses BASS_StreamCreateURL (not BASS_StreamCreateFile) — the latter
        // does not accept URLs and returns BASS_ERROR_ILLPARAM (20).
        int handle = await Task.Run(() =>
        {
            try
            {
                return BassNative.CreateUrlStream(url);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BASS Engine] HLS CreateUrlStream exception: {ex.Message}");
                return 0;
            }
        });

        if (handle == 0)
        {
            var err = BassNative.GetLastError();
            Debug.WriteLine($"[BASS Engine] HLS CreateUrlStream failed. URL='{url}', Error: {err}");
            throw new InvalidOperationException(
                $"Could not open HLS stream:\n{url}\n\n" +
                $"Reason: BASS error {err}. " +
                $"Verify basshls.dll (Windows) or libbasshls.so (Linux) is in the lib/ folder " +
                $"and that basshls was registered successfully on startup.");
        }

        // Log which decoder BASS chose (basshls, or a fallback decoder)
        try
        {
            BassNative.GetChannelInfo(handle, out var info);
            Debug.WriteLine($"[BASS Engine] HLS stream created: handle={handle}, " +
                            $"codec={info.ChannelType}, freq={info.Frequency}, " +
                            $"chans={info.Channels}, plugin={info.Plugin}.");
        }
        catch { }

        // NOTE: ICY metadata isn't applicable to HLS. HLS uses ID3 tags embedded
        // in segments for "Now Playing" metadata, which basshls surfaces via
        // SyncFlags.MetadataReceived. The existing _metaSyncProcedure (set up
        // in the constructor) already subscribes to this sync, so metadata
        // updates from HLS segments will fire OnBassMetaSync automatically.

        // No HttpClient state to clean up — BASS owns the network connection
        // for HLS. The connection is freed when StreamFree(handle) is called
        // by Stop/Dispose.
        return handle;
    }

    // ── HttpClient URL streaming ──
    //
    // Opens an HTTP connection with .NET's HttpClient and creates a BASS
    // user stream that reads from the network. BASS detects the audio
    // format from the initial bytes and decodes through its normal pipeline.
    //
    // This replaces BASS's built-in HTTP client (Bass.CreateStream(url)),
    // which stops reading after ~32KB when the server sends Connection: close.
    //
    // ── HLS exception ──
    //
    // HLS streams (.m3u8 / .m3u URLs) take a different path. The response body
    // is a playlist text file, not audio bytes — basshls must parse the playlist
    // and make its OWN HTTP requests to fetch each segment. With
    // BASS_StreamCreateFileUser (the HttpClient path), BASS only gets the
    // playlist bytes and can't fetch segments.
    //
    // For HLS, we use BASS's native URL streaming client (BassNative.CreateStream
    // with a URL) directly. When basshls is loaded, it intercepts the URL,
    // downloads the playlist, fetches segments, and decodes them. This works
    // because HLS makes many short HTTP requests (one per segment) — the
    // "32KB then close" bug only matters for long direct streams, not HLS.
    //
    // The User-Agent is set via BASS_SetConfigPtr(BASS_CONFIG_NET_AGENT, ua)
    // right before the CreateStream call. The per-host UA override map
    // (GetUserAgentForHost) is applied to HLS streams too, so CDNs that need
    // a bare UA (streamtheworld.com) or a browser UA (zeno.fm) are handled.
    private async Task<int> OpenUrlStreamAsync(string url)
    {
        // ── HLS path: route through BASS's native URL streaming client ──
        // basshls intercepts the URL and handles playlist parsing + segment
        // fetching internally. HttpClient is bypassed entirely.
        if (IsHlsUrl(url))
        {
            return await OpenHlsStreamAsync(url);
        }

        // ── Direct stream path: HttpClient + BASS_StreamCreateFileUser ──
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

        // User-Agent: per-host selection.
        //
        // Different streaming CDNs have OPPOSITE UA heuristics, so no single
        // string satisfies all of them:
        //
        //   - Zeno.Fm / SurferNetwork: 401s anything that doesn't look like a
        //     real desktop browser. Bare "Mozilla/5.0" gets 401; full Chrome
        //     or Firefox UA gets 302 → 200 OK with the live stream.
        //
        //   - StreamTheWorld / Triton MediaGateway: serves a 5-second preview
        //     (exactly 32768 bytes, then closes the connection) to anything
        //     that looks like a real browser. Bare "Mozilla/5.0" gets the
        //     continuous live stream; full Chrome UA gets the 32 KB preview.
        //
        // The default is a full desktop Chrome UA, which is what the majority
        // of stations (including Zeno.Fm, SurferNetwork, plain Icecast/
        // Shoutcast) expect. Known ad-stitching CDNs that route browser UAs
        // to a preview get an override via GetUserAgentForHost.
        //
        // Verified by curl against both stations in July 2026:
        //   - Zeno.FM with full Chrome UA:           446 KB in 20s (live)
        //   - Zeno.FM with bare Mozilla/5.0:         401 Unauthorized
        //   - StreamTheWorld with full Chrome UA:    32768 bytes (5s preview)
        //   - StreamTheWorld with bare Mozilla/5.0:  175 KB in 20s (live)
        string userAgent = GetUserAgentForHost(request.RequestUri);
        request.Headers.UserAgent.ParseAdd(userAgent);
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
        _fileCloseProc = new BassNative.BassFileProcClose(OnFileClose);
        _fileLenProc = new BassNative.BassFileProcLength(OnFileLength);
        _fileReadProc = new BassNative.BassFileProcRead(OnFileRead);
        _fileSeekProc = new BassNative.BassFileProcSeek(OnFileSeek);

        _fileProcs = new BassNative.BASS_FILEPROCS
        {
            close = _fileCloseProc,
            length = _fileLenProc,
            read = _fileReadProc,
            seek = _fileSeekProc,
        };

        // Create the BASS user stream.
        //
        // STREAMFILE_BUFFER (1): BASS pre-buffers in a background thread,
        //   matching how BassNative.CreateStream works internally. BASS calls
        //   our read callback to fill its buffer, then decodes from the buffer.
        //
        // BASS_STREAM_STATUS (0x800000): allows BASS to report download
        //   progress, which we don't use but is harmless.
        //
        // BASS does format detection from the first bytes read — it will try
        // basshls (if the response is an m3u8 playlist), bass_aac (if AAC),
        // bassopus (if Opus), or Media Foundation (Windows built-in) in that
        // order. This happens automatically inside BASS_StreamCreateFileUser.
        int handle = BassNative.StreamCreateFileUser(
            BassNative.STREAMFILE_BUFFER,
            BassNative.BASS_STREAM_STATUS,
            ref _fileProcs,
            IntPtr.Zero);

        if (handle == 0)
        {
            Debug.WriteLine($"[BASS Engine] BASS_StreamCreateFileUser failed. Error: {BassNative.GetLastError()}");
            CleanupHttpStream();
        }
        else
        {
            // Log which decoder BASS chose.
            try
            {
                BassNative.GetChannelInfo(handle, out var info);
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
            catch (Exception ex) when (
                ex is OperationCanceledException ||
                ex is ObjectDisposedException ||
                ex is System.IO.IOException { InnerException: System.Net.Sockets.SocketException })
            {
                // Stream was cancelled or disposed during shutdown — expected path.
                // Return whatever we've already read so BASS can play it out,
                // then 0 on the next call (stream is null/disposed) signals EOF.
                return totalRead;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BASS Engine] OnFileRead unexpected error: {ex.GetType().Name}: {ex.Message}");
                return totalRead > 0 ? totalRead : 0;
            }

            if (read == 0)
            {
                // End of stream — server closed the connection.
                var elapsed = DateTime.UtcNow - _streamStartTimestamp;
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [BASS Engine] Network stream ended (server closed). " +
                                $"Received {_streamBytesReceived:N0} bytes in {elapsed.TotalSeconds:F1}s.");
                break;
            }

            totalRead += read;
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
            IntPtr ptr = BassNative.ChannelGetTags(channel, BassNative.BassTagType.META);
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
            // stream. BassNative.StreamFree would auto-remove them, but explicit
            // removal guarantees any in-flight callback has been drained
            // before the stream memory is released. This is the belt-and-
            // suspenders companion to the OnDsp local-copy fix.
            if (_dspHandle != 0)
            {
                try { BassNative.ChannelRemoveDSP(_bassStream, _dspHandle); }
                catch (Exception ex) { Debug.WriteLine($"[BASS Engine] ChannelRemoveDSP failed: {ex.Message}"); }
                _dspHandle = 0;
            }
            if (_endSyncHandle != 0)
            {
                try { BassNative.ChannelRemoveSync(_bassStream, _endSyncHandle); }
                catch (Exception ex) { Debug.WriteLine($"[BASS Engine] ChannelRemoveSync failed: {ex.Message}"); }
                _endSyncHandle = 0;
            }
            if (_metaSyncHandle != 0)
            {
                try { BassNative.ChannelRemoveSync(_bassStream, _metaSyncHandle); }
                catch (Exception ex) { Debug.WriteLine($"[BASS Engine] ChannelRemoveSync (meta) failed: {ex.Message}"); }
                _metaSyncHandle = 0;
            }

            try { BassNative.StreamFree(_bassStream); }
            catch (Exception ex) { Debug.WriteLine($"[BASS Engine] StreamFree failed: {ex.Message}"); }
            _bassStream = 0;
            _eqFxHandle = 0;
        }

        if (IsAvailable && _ownsBassContext)
        {
            BassNative.Free();
        }

        IsAvailable = false;
    }
    #endregion
}
