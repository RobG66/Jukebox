using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jukebox.Extensions;
using Jukebox.Models;
using Jukebox.Mpv;
using Jukebox.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jukebox.ViewModels;

public partial class JukeboxViewModel
{
    #region Fields
    private readonly HashSet<JukeboxTrack> _playedTracks = new();
    private readonly Random _random = new();
    private DispatcherTimer? _playbackTimer;
    private bool _isTimerUpdating;
    private readonly TaskCompletionSource _backendReadyTcs = new();

    private JukeboxTrack? _subscribedTrack;
    private bool _isPlaybackDisposed;

    private readonly BassPlaybackEngine _bassEngine = new();
    private readonly MpvPlaybackEngine _mpvEngine = new();
    private IMediaPlayerEngine? _activeEngine;

    // Cancellation for in-flight URL-stream connection attempts.
    //
    // Each call to StartTrackAsync that targets a URL stream creates a new
    // CTS and stores it here. The next StartTrackAsync invocation cancels
    // the previous CTS first, which:
    //   - Aborts the BassPlaybackEngine.OpenUrlStreamAsync HTTP send
    //     (it awaits _httpClient.SendAsync with this token).
    //   - Causes the previous StartTrackAsync's PlayAsync await to throw
    //     OperationCanceledException, which its catch block recognizes as
    //     "superseded by a newer call" and silently bails out — no error
    //     dialog, no transport-button reset.
    //
    // This solves the "if one connection is waiting, nothing else can play
    // until first times out" problem: clicking a new station while the old
    // one is still connecting cancels the old connection immediately.
    private CancellationTokenSource? _streamConnectCts;

    public event EventHandler<short[]>? PcmDataAvailable;
    #endregion

    #region Observable Properties
    [ObservableProperty] private bool _isBackendReady;
    [ObservableProperty] private bool _isInitializing;
    [ObservableProperty] private bool _isBassAvailable;
    [ObservableProperty] private bool _isMpvAvailable;
    [ObservableProperty] private bool _isVisualizerVisible = true;
    [ObservableProperty] private bool _isVisualizerEnabled = true;
    [ObservableProperty] private bool _isCurrentTrackStream;
    [ObservableProperty] private string _currentTimeString = "0:00";
    [ObservableProperty] private string _totalTimeString = "0:00";
    [ObservableProperty] private double _playbackLength = 100;
    [ObservableProperty] private bool _canPlay = false;
    [ObservableProperty] private bool _canPause = false;
    [ObservableProperty] private bool _canStop = false;
    [ObservableProperty] private bool _isSeeking = false;

    [ObservableProperty]
    private JukeboxTrack? _currentTrack = new() { DisplayName = "No Track Loaded" };

    private double _playbackPosition;
    public double PlaybackPosition
    {
        get => _playbackPosition;
        set
        {
            if (SetProperty(ref _playbackPosition, value))
            {
                if (!_isTimerUpdating && !IsSeeking)
                    SeekToPosition(value);
            }
        }
    }

    partial void OnIsSeekingChanged(bool value)
    {
        if (!value) SeekToPosition(PlaybackPosition);
    }

    private double _volume = 100;
    public double Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, value))
            {
                _activeEngine?.SetVolume(value);
            }
        }
    }
    #endregion

    #region Public Properties
    public MpvContext? MpvContext => _mpvEngine.MpvContext;
    #endregion

    #region Initialization
    public async Task InitializeBackendAsync()
    {
        Dispatcher.UIThread.Post(() => IsInitializing = true);
        var sw = Stopwatch.StartNew();
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] Starting Backend Initialization...");

        EqViewModel.EqBandUpdated += OnEqBandUpdated;

        _playbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Constants.PlaybackTimerIntervalMs)
        };
        _playbackTimer.Tick += PlaybackTimer_Tick;

        await PlaylistViewModel.InitializeAsync();

        await Task.Run(() =>
        {
            _bassEngine.Initialize();
            _mpvEngine.Initialize();
        });

        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] Backend Initialization completed in {sw.ElapsedMilliseconds}ms overall.");

        var projectMPath = Jukebox.Services.PathProvider.Current.ProjectMPresetsDirectory;
        var projectMExists = System.IO.Directory.Exists(projectMPath);
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] ProjectM presets path: {projectMPath} exists? {projectMExists}");

        var visualizerAvailable = this.VisualizerRuntime.IsAvailable && !IsVisualizerDisabled;
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] Visualizer runtime available? {this.VisualizerRuntime.IsAvailable} (disabled by switch? {IsVisualizerDisabled} → effective: {visualizerAvailable})");

        Dispatcher.UIThread.Post(() =>
        {
            IsBassAvailable = _bassEngine.IsAvailable;
            IsMpvAvailable = _mpvEngine.IsAvailable;
            IsVisualizerAvailable = visualizerAvailable;
            IsBackendReady = true;
            IsInitializing = false;
            _backendReadyTcs.TrySetResult();
            InitializeStartupAsync().SafeFireAndForget(nameof(InitializeStartupAsync));
        });
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (IsSeeking || _activeEngine == null || IsCurrentTrackStream) return;

        _isTimerUpdating = true;
        try
        {
            double positionMs = _activeEngine.GetPositionMs();
            if (positionMs >= 0)
            {
                PlaybackPosition = positionMs;
                CurrentTimeString = TimeSpan.FromMilliseconds(positionMs).ToString(@"m\:ss");
            }
        }
        finally
        {
            _isTimerUpdating = false;
        }
    }
    #endregion

    #region Playback Commands
    [RelayCommand]
    private async Task PlayAsync()
    {
        if (!CanPlay) return;

        if (CanStop && CurrentTrack != null)
        {
            _activeEngine?.Resume();
            _playbackTimer?.Start();
            CanPlay = false;
            CanPause = true;
            return;
        }

        if (CurrentTrack == null || string.IsNullOrEmpty(CurrentTrack.FilePath))
        {
            if (PlaylistViewModel.Playlist.Count > 0)
                CurrentTrack = PlaylistViewModel.Playlist[0];
            else
                return;
        }

        await StartTrackAsync();
    }

    [RelayCommand]
    private async Task PlayTrackAsync(JukeboxTrack track)
    {
        CurrentTrack = track;
        await StartTrackAsync();
    }

    [RelayCommand]
    private void Pause()
    {
        _activeEngine?.Pause();
        _playbackTimer?.Stop();
        CanPlay = true;
        CanPause = false;
        CanStop = true;
    }

    [RelayCommand]
    private void Stop()
    {
        _playbackTimer?.Stop();
        _activeEngine?.Stop();

        // Cancel any in-flight URL-stream connection attempt — covers the
        // user pressing Stop while a stream is still connecting. The
        // StartTrackAsync await will throw OperationCanceledException and
        // bail out silently.
        _streamConnectCts?.Cancel();
        _streamConnectCts?.Dispose();
        _streamConnectCts = null;

        // Clear any external token we handed to the BASS engine so it
        // doesn't leak into a subsequent local-file playback.
        if (_activeEngine is BassPlaybackEngine bassForClear)
        {
            bassForClear.SetStreamCancellationToken(CancellationToken.None);
        }

        // Make sure the connecting overlay is cleared whenever playback is
        // stopped — covers the user pressing Stop while a connection is in
        // progress (the StartTrackAsync try/finally will also clear it, but
        // this is a belt-and-suspenders guard against any future code path
        // that bypasses that cleanup).
        IsConnecting = false;
        ConnectingMessage = "";

        CanPlay = PlaylistViewModel.Playlist.Count > 0;
        CanPause = false;
        CanStop = false;
        CurrentTimeString = "0:00";
        PlaybackPosition = 0;
    }

    [RelayCommand]
    private async Task PreviousAsync()
    {
        if (PlaylistViewModel.Playlist.Count == 0) return;
        var index = CurrentTrack != null ? PlaylistViewModel.Playlist.IndexOf(CurrentTrack) : -1;

        if (index > 0)
            CurrentTrack = PlaylistViewModel.Playlist[index - 1];
        else if (IsLoopEnabled)
            CurrentTrack = PlaylistViewModel.Playlist[^1];
        else
            return;

        await StartTrackAsync();
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (PlaylistViewModel.Playlist.Count == 0) return;

        var next = PickNextTrack(IsRandomPlayback);
        if (next == null) { Stop(); return; }

        CurrentTrack = next;
        await StartTrackAsync();
    }
    #endregion

    #region Core Playback
    private async Task StartTrackAsync()
    {
        if (CurrentTrack == null || string.IsNullOrEmpty(CurrentTrack.FilePath)) return;

        // Cancel any in-flight URL-stream connection attempt from a previous
        // StartTrackAsync call. This covers:
        //   - User picks radio station B while station A is still connecting.
        //   - User picks a local file while a radio station is still connecting.
        // The cancelled previous call's PlayAsync await will throw
        // OperationCanceledException, which its catch block recognizes and
        // silently bails on (no error dialog, no transport reset).
        //
        // We do this BEFORE the engine.Stop() call below so the engine's
        // HTTP send aborts at the same time as the engine's internal state
        // is being torn down — no race between the two cleanups.
        _streamConnectCts?.Cancel();
        _streamConnectCts?.Dispose();
        _streamConnectCts = null;
        // Clear the BASS engine's external token so the engine doesn't
        // observe a stale cancelled token on the next local-file PlayAsync.
        // (If the new track is itself a URL stream, we'll set a fresh token
        // further below.)
        _bassEngine.SetStreamCancellationToken(CancellationToken.None);

        if (_activeEngine != null)
        {
            _activeEngine.Stop();
            _activeEngine.PlaybackEnded -= OnEnginePlaybackEnded;
            _activeEngine.DurationChanged -= OnEngineDurationChanged;
            _activeEngine.MetadataChanged -= OnEngineMetadataChanged;
            if (_activeEngine is BassPlaybackEngine oldBass)
            {
                oldBass.PcmDataAvailable -= OnBassPcmDataAvailable;
            }
            else if (_activeEngine is VgmPlaybackEngine oldVgm)
            {
                oldVgm.PcmDataAvailable -= OnBassPcmDataAvailable;
            }
        }

        bool isUrl = CurrentTrack.FilePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     CurrentTrack.FilePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        // Determine which URLs should NOT go to BASS.
        //
        // .pls is a meta-playlist (Shoutcast/Icecast playlist format) — it's
        // a small text file listing actual stream URLs, not a media stream.
        // BASS doesn't parse .pls directly, so these are routed to MPV which
        // resolves them via ffmpeg.
        //
        // .ashx is an ASP.NET handler used by some stations as a redirect
        // endpoint — also routed to MPV.
        //
        // All other URL streams (including StreamTheWorld/Triton) are routed
        // to BASS. BASS uses our HttpClient-based streaming (see
        // BassPlaybackEngine.OpenUrlStreamAsync) which handles Connection:
        // close responses correctly — BASS's built-in HTTP client stops
        // reading after ~32KB when it sees Connection: close, but HttpClient
        // keeps reading until the socket actually closes. This means
        // visualizations and EQ work for all radio stations, including
        // StreamTheWorld.
        bool needsMpv = isUrl && (
            CurrentTrack.FilePath.Contains(".pls", StringComparison.OrdinalIgnoreCase) ||
            CurrentTrack.FilePath.Contains(".ashx", StringComparison.OrdinalIgnoreCase)
        );

        bool isAudioExtension = Constants.AudioExtensions.Any(ext =>
            CurrentTrack.FilePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        // Route URL streams to BASS (isAudio = true) unless they need MPV.
        // BASS natively handles MP3, AAC, Opus, Shoutcast, and (via basshls)
        // HLS streams — all with visualizations and EQ.
        bool isAudio = isUrl ? !needsMpv : isAudioExtension;

        IsVisualizerVisible = isAudio;

        // Route VGM/VGZ/VGX tracks (local .vgm/.vgz/.vgx files) to the VGM engine if available.
        bool isVgm = CurrentTrack.FilePath.EndsWith(".vgz", StringComparison.OrdinalIgnoreCase) ||
                     CurrentTrack.FilePath.EndsWith(".vgm", StringComparison.OrdinalIgnoreCase) ||
                     CurrentTrack.FilePath.EndsWith(".vgx", StringComparison.OrdinalIgnoreCase);
        Debug.WriteLine($"[Playback] FilePath={CurrentTrack.FilePath}");
        Debug.WriteLine($"[Playback] isVgm={isVgm}, VgmEngine={(VgmEngine != null ? "not null" : "NULL")}");
        Debug.WriteLine($"[Playback] Routing to: {(isVgm && VgmEngine is not null ? "VGM" : (isAudio ? "BASS" : "MPV"))} engine");

        if (isVgm && VgmEngine is not null)
            _activeEngine = VgmEngine;
        else if (isAudio)
            _activeEngine = _bassEngine;
        else
            _activeEngine = _mpvEngine;
        _activeEngine.PlaybackEnded += OnEnginePlaybackEnded;
        _activeEngine.DurationChanged += OnEngineDurationChanged;
        _activeEngine.MetadataChanged += OnEngineMetadataChanged;

        IsCurrentTrackStream = isUrl;
        if (isUrl)
        {
            CurrentTimeString = "—";
            TotalTimeString = "—";
            PlaybackPosition = 0;
            PlaybackLength = 0;
        }

        _activeEngine.SetVolume(Volume);

        // ── URL stream connection lifecycle ──
        //
        // For URL streams (radio), PlayAsync only confirms the HTTP
        // connection was opened and BASS accepted the stream handle —
        // actual audio may not flow for another second or two while
        // BASS buffers and detects the codec. We show a "Connecting..."
        // overlay and race PlayAsync + the engine's PlaybackStarted
        // event against a 15-second timeout (Constants.StreamConnectionTimeoutMs).
        //
        // Failure modes covered:
        //   - SSL/TLS errors (e.g. expired server cert) — PlayAsync throws.
        //   - DNS / connect / HTTP 4xx/5xx — PlayAsync throws.
        //   - Server accepts the connection but never sends audio bytes
        //     — PlaybackStarted never fires; the 15s timeout aborts.
        //   - User starts a different track while this connection is still
        //     opening — the new StartTrackAsync call cancels our
        //     _streamConnectCts, PlayAsync throws OperationCanceledException,
        //     and we bail out silently without showing an error dialog.
        //
        // We deliberately do NOT fall back to MPV for failed radio
        // streams — the user explicitly asked for this behavior. MPV is
        // only used for URL streams when the URL itself is .pls/.ashx
        // (those need MPV's ffmpeg-based resolver).
        //
        // Capture the local token at entry; if a newer StartTrackAsync
        // call swaps _streamConnectCts out from under us, our local
        // reference still points to the (now-cancelled) old CTS so the
        // cancellation check below fires correctly.
        CancellationToken? connectToken = null;
        if (isUrl)
        {
            // Create a fresh CTS for this connection attempt. The previous
            // attempt's CTS was already cancelled at the top of StartTrackAsync,
            // so we just need a new one here.
            _streamConnectCts = new CancellationTokenSource();
            connectToken = _streamConnectCts.Token;

            // Hand the token to the BASS engine so OpenUrlStreamAsync can
            // link it into the HttpClient.SendAsync call. Only BASS uses it
            // (MPV/pls/.ashx paths don't go through this code path).
            if (_activeEngine is BassPlaybackEngine bassForToken)
            {
                bassForToken.SetStreamCancellationToken(connectToken.Value);
            }

            ConnectingMessage = $"Connecting to {ExtractStreamHost(CurrentTrack.FilePath)}...";
            IsConnecting = true;
        }

        // One-shot TCS set when the engine raises PlaybackStarted (first
        // PCM buffer for BASS/VGM, first positive time-pos for MPV).
        var playbackStartedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler startedHandler = (_, _) => playbackStartedTcs.TrySetResult(true);
        _activeEngine.PlaybackStarted += startedHandler;

        Exception? playError = null;
        bool timedOut = false;
        bool cancelled = false;
        try
        {
            // PlayAsync itself may throw (HTTP/SSL failure happens inside
            // BassPlaybackEngine.OpenUrlStreamAsync).
            await _activeEngine.PlayAsync(CurrentTrack);

            // If our connection token was cancelled while PlayAsync was
            // running (a newer StartTrackAsync superseded us), bail out
            // silently. Don't even reach the timeout wait.
            if (connectToken is { } t && t.IsCancellationRequested)
            {
                cancelled = true;
                Debug.WriteLine("[Playback] Connection superseded by a newer StartTrackAsync call (post-PlayAsync).");
            }
            else if (isUrl)
            {
                // PlayAsync returned without throwing — now wait for
                // PlaybackStarted, but cap the wait at 15 seconds. We also
                // race against connectToken so a superseding call unblocks
                // us immediately instead of waiting the full 15s.
                Task timeoutTask = Task.Delay(Constants.StreamConnectionTimeoutMs);
                Task cancelTask = connectToken is { } ct2
                    ? Task.Delay(Timeout.Infinite, ct2)
                    : Task.FromResult(false);
                var winner = await Task.WhenAny(
                    playbackStartedTcs.Task,
                    timeoutTask,
                    cancelTask);

                if (winner == playbackStartedTcs.Task)
                {
                    // Success — playback actually started.
                }
                else if (winner == cancelTask && connectToken!.Value.IsCancellationRequested)
                {
                    cancelled = true;
                    Debug.WriteLine("[Playback] Connection superseded by a newer StartTrackAsync call (during PlaybackStarted wait).");
                }
                else
                {
                    timedOut = true;
                    Debug.WriteLine($"[Playback] Stream connection timed out after {Constants.StreamConnectionTimeoutMs}ms with no audio.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Our connectToken was cancelled — a newer StartTrackAsync call
            // superseded us. Bail out silently; the new call is responsible
            // for all subsequent UI state.
            cancelled = true;
            Debug.WriteLine("[Playback] Connection cancelled (OperationCanceledException) — superseded by a newer StartTrackAsync call.");
        }
        catch (Exception ex)
        {
            playError = ex;
            Debug.WriteLine($"[Playback] PlayAsync threw: {ex.Message}");
        }
        finally
        {
            _activeEngine.PlaybackStarted -= startedHandler;
        }

        // Clean up the connecting overlay regardless of outcome — it must
        // never get stuck on screen. (If we were superseded, the new call
        // has already set IsConnecting=true with its own message; clearing
        // here is harmless because the new call's set comes after.)
        if (!cancelled)
        {
            IsConnecting = false;
            ConnectingMessage = "";
        }

        // If we were cancelled (superseded), bail out silently. Do NOT show
        // an error dialog, do NOT reset transport buttons — the new
        // StartTrackAsync call owns all of that now.
        if (cancelled)
        {
            // Detach our event handlers from this engine since we're done
            // with this attempt. The new call will have attached its own
            // handlers (possibly to a different engine entirely).
            _activeEngine.PlaybackEnded -= OnEnginePlaybackEnded;
            _activeEngine.DurationChanged -= OnEngineDurationChanged;
            _activeEngine.MetadataChanged -= OnEngineMetadataChanged;
            if (_activeEngine is BassPlaybackEngine cancelledBass)
            {
                cancelledBass.PcmDataAvailable -= OnBassPcmDataAvailable;
            }
            else if (_activeEngine is VgmPlaybackEngine cancelledVgm)
            {
                cancelledVgm.PcmDataAvailable -= OnBassPcmDataAvailable;
            }
            return;
        }

        if (playError is not null || timedOut)
        {
            // Detach the engine event handlers BEFORE calling Stop(). The
            // BASS End sync fires when StreamFree runs (which Stop calls
            // internally), and if our PlaybackEnded handler is still
            // attached it would dispatch to the UI thread and advance to
            // the next track — wrong behavior after a failed connection.
            _activeEngine.PlaybackEnded -= OnEnginePlaybackEnded;
            _activeEngine.DurationChanged -= OnEngineDurationChanged;
            _activeEngine.MetadataChanged -= OnEngineMetadataChanged;
            if (_activeEngine is BassPlaybackEngine failedBass)
            {
                failedBass.PcmDataAvailable -= OnBassPcmDataAvailable;
            }
            else if (_activeEngine is VgmPlaybackEngine failedVgm)
            {
                failedVgm.PcmDataAvailable -= OnBassPcmDataAvailable;
            }

            // Abort any partial playback state and surface the failure
            // to the user via a single-button (OK) error dialog.
            try { _activeEngine.Stop(); } catch { /* swallow — best-effort cleanup */ }

            string title = timedOut ? "Connection Unsuccessful" : "Playback Error";
            string reason = timedOut
                ? $"Timed out after {Constants.StreamConnectionTimeoutMs / 1000} seconds with no audio from the stream."
                : UnwrapRootErrorMessage(playError);

            await Jukebox.Views.ThreeButtonDialogView.ShowErrorAsync(
                title,
                $"Could not play '{CurrentTrack.DisplayName}':\n{CurrentTrack.FilePath}\n\nReason: {reason}");

            // Reset the transport buttons — nothing is playing.
            CanPlay = PlaylistViewModel.Playlist.Count > 0;
            CanPause = false;
            CanStop = false;
            return; // do NOT fall through to EQ init / SetPlayingState
        }

        // Success — fall through to EQ init and SetPlayingState below.

        // InitializeEqBands must be called AFTER PlayAsync.
        //
        // BassPlaybackEngine.InitializeEqBands guards on _bassStream != 0,
        // and the stream is created inside PlayAsync (Bass.CreateStream).
        // Calling InitializeEqBands before PlayAsync silently no-ops because
        // _bassStream is still 0 at that point. This means saved EQ gains
        // were never applied at track start.
        //
        // Now we initialize EQ after the stream is alive. InitializeEqBands
        // internally guards on _bassStream != 0, and after PlayAsync returns
        // the stream is guaranteed to be non-zero (or PlayAsync already
        // returned early with an error dialog).
        if (_activeEngine is BassPlaybackEngine newBass)
        {
            newBass.PcmDataAvailable += OnBassPcmDataAvailable;

            var gains = new double[Constants.EqBandCount];
            var centerFreqs = new float[Constants.EqBandCount];
            for (int i = 0; i < Constants.EqBandCount; i++)
            {
                if (EqViewModel.EqBands.Count > i)
                {
                    gains[i] = EqViewModel.EqBands[i].Gain;
                    centerFreqs[i] = EqViewModel.EqBands[i].CenterFrequency;
                }
            }
            newBass.InitializeEqBands(gains, centerFreqs);
        }
        else if (_activeEngine is VgmPlaybackEngine vgm)
        {
            vgm.PcmDataAvailable += OnBassPcmDataAvailable;

            var gains = new double[Constants.EqBandCount];
            var centerFreqs = new float[Constants.EqBandCount];
            for (int i = 0; i < Constants.EqBandCount; i++)
            {
                if (EqViewModel.EqBands.Count > i)
                {
                    gains[i] = EqViewModel.EqBands[i].Gain;
                    centerFreqs[i] = EqViewModel.EqBands[i].CenterFrequency;
                }
            }
            vgm.InitializeEqBands(gains, centerFreqs);
        }

        SetPlayingState();
    }

    private void SeekToPosition(double positionMs)
    {
        _activeEngine?.Seek(positionMs);
    }

    /// <summary>
    /// Extracts a short "host:port" string from a URL for display in the
    /// "Connecting to ..." overlay. Falls back to the raw URL if parsing
    /// fails — never throws.
    /// </summary>
    private static string ExtractStreamHost(string url)
    {
        try
        {
            // Uri tries to parse "scheme://host:port/path?query". We only
            // want the authority (host:port) component — the full path is
            // too long/noisy for a transient overlay message.
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Authority))
            {
                return uri.Authority; // e.g. "stream.radiox.sk:8443"
            }
        }
        catch
        {
            // Best-effort — fall through to the raw URL.
        }
        return url;
    }

    /// <summary>
    /// Extracts the actual root cause message from a (possibly nested)
    /// playback exception, stripping Jukebox-internal wrapper text.
    /// </summary>
    /// <remarks>
    /// The BASS engine wraps errors as plain <c>InvalidOperationException</c>
    /// instances (no <c>InnerException</c> set), with each layer appending:
    ///   "Could not open ... stream:\n&lt;url&gt;\n\nReason: &lt;inner.Message&gt;"
    /// The outermost exception's <c>Message</c> thus ends up reading:
    /// <code>
    /// Could not open or resolve audio stream:
    /// https://stream.radiox.sk:8443/mood.mp3
    ///
    /// Reason: Could not open radio stream:
    /// https://stream.radiox.sk:8443/mood.mp3
    ///
    /// Reason: The SSL connection could not be established, see inner exception.
    /// </code>
    /// Without unwrapping, the user sees this triple-stacked cascade in the
    /// error dialog. This helper finds the LAST "Reason:" delimiter in the
    /// message and returns what follows, which is the actual root cause
    /// (e.g. the SSL message). If there's no "Reason:" delimiter, the
    /// original message is returned unchanged.
    /// </remarks>
    private static string UnwrapRootErrorMessage(Exception? ex)
    {
        if (ex is null) return "Unknown error.";

        // Walk to the innermost exception first — some throw sites may set
        // InnerException properly, in which case we want the leaf.
        var current = ex;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        var msg = current.Message?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(msg)) return "Unknown error.";

        // Find the LAST "Reason:" delimiter — everything after it is the
        // actual root cause text. LastIndexOf is correct here because the
        // cascade is left-to-right, outer-to-inner, so the last "Reason:"
        // is immediately followed by the leaf cause.
        var reasonIdx = msg.LastIndexOf("Reason:", StringComparison.OrdinalIgnoreCase);
        if (reasonIdx >= 0 && reasonIdx + "Reason:".Length < msg.Length)
        {
            var peeled = msg.Substring(reasonIdx + "Reason:".Length).Trim();
            if (!string.IsNullOrEmpty(peeled)) msg = peeled;
        }

        return msg;
    }

    private void UpdateTrackDuration(double durationMs)
    {
        if (IsCurrentTrackStream)
        {
            PlaybackLength = 0;
            TotalTimeString = "—";
        }
        else
        {
            PlaybackLength = durationMs;
            TotalTimeString = TimeSpan.FromMilliseconds(durationMs).ToString(@"m\:ss");
        }
    }

    private void SetPlayingState()
    {
        _playbackTimer?.Start();
        CanPlay = false;
        CanPause = true;
        CanStop = true;
    }

    private JukeboxTrack? PickNextTrack(bool random)
    {
        var playlist = PlaylistViewModel.Playlist;

        if (random)
        {
            if (CurrentTrack != null) _playedTracks.Add(CurrentTrack);

            var available = playlist.Where(t => !_playedTracks.Contains(t)).ToList();

            if (available.Count == 0)
            {
                if (!IsLoopEnabled) return null;
                _playedTracks.Clear();
                if (CurrentTrack != null) _playedTracks.Add(CurrentTrack);
                available = playlist.Where(t => !_playedTracks.Contains(t)).ToList();
                if (available.Count == 0) available = playlist.ToList();
            }

            return available[_random.Next(available.Count)];
        }
        else
        {
            var index = CurrentTrack != null ? playlist.IndexOf(CurrentTrack) : -1;
            if (index >= 0 && index < playlist.Count - 1)
                return playlist[index + 1];
            return IsLoopEnabled ? playlist[0] : null;
        }
    }
    #endregion

    #region Track Changed
    partial void OnCurrentTrackChanged(JukeboxTrack? value)
    {
        if (_subscribedTrack != null && !ReferenceEquals(_subscribedTrack, value))
            _subscribedTrack.PropertyChanged -= CurrentTrack_PropertyChanged;

        _subscribedTrack = value;

        foreach (var track in PlaylistViewModel.Playlist)
        {
            track.IsPlaying = (track == value);
        }

        if (value == null) return;

        value.PropertyChanged += CurrentTrack_PropertyChanged;

        if (ShowPlayingMode != ShowPlayingMode.Off)
        {
            bool always = (ShowPlayingMode == ShowPlayingMode.Always);
            _showPlayingService.ShowAsync(value.DisplayName, ShowPlayingTimeout, always)
                .SafeFireAndForget(nameof(_showPlayingService.ShowAsync));
        }

        if (!string.IsNullOrEmpty(value.FilePath))
        {
            bool isStream = value.FilePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                            value.FilePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            if (isStream)
            {
                PlaybackLength = 0;
                TotalTimeString = "—";
            }
            else
            {
                PlaybackLength = value.Length.TotalMilliseconds;
                TotalTimeString = value.DisplayLength;
            }
        }
    }

    private void CurrentTrack_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JukeboxTrack.Length) && CurrentTrack != null)
        {
            bool isStream = CurrentTrack.FilePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                            CurrentTrack.FilePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            if (isStream)
            {
                PlaybackLength = 0;
                TotalTimeString = "—";
            }
            else
            {
                PlaybackLength = CurrentTrack.Length.TotalMilliseconds;
                TotalTimeString = CurrentTrack.DisplayLength;
            }
        }
    }

    private void OnEngineMetadataChanged(object? sender, string metadata)
    {
        if (ShowPlayingMode != ShowPlayingMode.Off)
        {
            bool always = (ShowPlayingMode == ShowPlayingMode.Always);
            _showPlayingService.ShowAsync(metadata, ShowPlayingTimeout, always)
                .SafeFireAndForget(nameof(_showPlayingService.ShowAsync));
        }
    }
    #endregion

    #region Callbacks
    private void OnEnginePlaybackEnded(object? sender, EventArgs e)
    {
        Debug.WriteLine("[Playback] OnEnginePlaybackEnded fired — dispatching to UI thread.");
        Dispatcher.UIThread.Post(async () =>
        {
            Debug.WriteLine($"[Playback] PlaybackEnded handler running. IsRepeat={IsRepeatEnabled}, IsRandom={IsRandomPlayback}, PlaylistCount={PlaylistViewModel.Playlist.Count}");

            if (IsRepeatEnabled)
            {
                Debug.WriteLine("[Playback] Repeat enabled — replaying current track.");
                await StartTrackAsync();
            }
            else
            {
                var next = PickNextTrack(IsRandomPlayback);
                if (next != null)
                {
                    Debug.WriteLine($"[Playback] Next track: {next.DisplayName}");
                    CurrentTrack = next;
                    await StartTrackAsync();
                }
                else
                {
                    Debug.WriteLine("[Playback] No next track — stopping.");
                    CurrentTrack = null;
                    Stop();
                }
            }
        });
    }

    private void OnEngineDurationChanged(object? sender, double durationMs)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateTrackDuration(durationMs);
            if (CurrentTrack != null)
            {
                CurrentTrack.Length = TimeSpan.FromMilliseconds(durationMs);
            }
        });
    }

    private void OnBassPcmDataAvailable(object? sender, short[] pcm)
    {
        PcmDataAvailable?.Invoke(this, pcm);
    }

    private void OnEqBandUpdated(object? sender, EqSliderViewModel band)
    {
        int index = EqViewModel.EqBands.IndexOf(band);
        if (index >= 0 && index < Constants.EqBandCount)
        {
            if (_activeEngine is BassPlaybackEngine bass)
            {
                bass.UpdateEqBand(index, band.Gain, band.CenterFrequency);
            }
            else if (_activeEngine is VgmPlaybackEngine vgm)
            {
                vgm.UpdateEqBand(index, band.Gain, band.CenterFrequency);
            }
        }
    }
    #endregion

    #region Public API
    public async Task InitializeStartupAsync()
    {
        Volume = InitialVolume;
        if (!string.IsNullOrEmpty(InitialFile))
        {
            await PlaylistViewModel.ProcessAndAddFilesAsync(new List<string> { InitialFile }, NoRecurse);
            if (PlaylistViewModel.Playlist.Count > 0)
            {
                CurrentTrack = PlaylistViewModel.Playlist[0];
                await StartTrackAsync();
            }
        }
    }

    public async Task PlayMediaFilesAsync(string[] mediaFiles, bool autoPlay)
    {
        await _backendReadyTcs.Task;
        await PlaylistViewModel.ProcessAndAddFilesAsync(mediaFiles.ToList(), NoRecurse);
        if (autoPlay && PlaylistViewModel.Playlist.Count > 0)
        {
            CurrentTrack = PlaylistViewModel.Playlist[0];
            await StartTrackAsync();
        }
    }

    public void LoadSystemLogo(string systemName)
    {
        Debug.WriteLine($"[WARN] LoadSystemLogo('{systemName}') is not implemented.");
    }
    #endregion

    #region Dispose
    public async Task DisposePlaybackAsync()
    {
        if (_isPlaybackDisposed) return;
        _isPlaybackDisposed = true;

        try
        {
            if (_subscribedTrack != null)
                _subscribedTrack.PropertyChanged -= CurrentTrack_PropertyChanged;

            EqViewModel.EqBandUpdated -= OnEqBandUpdated;

            // Stop the playback timer before disposing engines. The timer
            // is a DispatcherTimer (fires on UI thread), and DisposePlaybackAsync
            // also runs on the UI thread, so there is no race between
            // PlaybackTimer_Tick and the dispose — they're serialized by the
            // UI thread's message pump. The timer.Stop() call ensures no
            // further ticks fire after this point.
            //
            // (Note: ARCHITECTURE.md previously claimed a _bassStreamLock
            // protected this path — that lock never existed in the code. The
            // actual safety comes from DispatcherTimer's UI-thread affinity,
            // which is sufficient.)
            _playbackTimer?.Stop();

            // Cancel any in-flight URL-stream connection attempt so the
            // engine's HTTP send unblocks immediately during shutdown.
            _streamConnectCts?.Cancel();
            _streamConnectCts?.Dispose();
            _streamConnectCts = null;

            if (_activeEngine != null)
            {
                _activeEngine.PlaybackEnded -= OnEnginePlaybackEnded;
                _activeEngine.DurationChanged -= OnEngineDurationChanged;
                _activeEngine.MetadataChanged -= OnEngineMetadataChanged;
                if (_activeEngine is BassPlaybackEngine bass)
                {
                    bass.PcmDataAvailable -= OnBassPcmDataAvailable;
                }
                else if (_activeEngine is VgmPlaybackEngine vgm)
                {
                    vgm.PcmDataAvailable -= OnBassPcmDataAvailable;
                }
            }

            // Both engine dispose calls are synchronous.
            // This avoids racing with the window close and leaving native MPV
            // resources being freed after process exit. The synchronous call
            // blocks for ~50-100ms during close — acceptable within the
            // 3-second DisposeTimeoutMs cap in JukeboxView.CloseAsync.
            _bassEngine.Dispose();
            _mpvEngine.Dispose();
            VgmEngine?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Dispose] Error cleaning up playback backend: {ex.Message}");
        }

        await Task.CompletedTask;
    }
    #endregion
}
