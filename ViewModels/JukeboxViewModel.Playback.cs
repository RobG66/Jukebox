using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jukebox.Extensions;
using Jukebox.Models;
using Jukebox.Mpv;
using Jukebox.Plugin.Abstractions;
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
    private long _playGeneration;
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

    // Cancellation for plugin URL resolution. Resolution happens before an
    // engine is selected, so it has a separate lifetime from stream opening.
    private CancellationTokenSource? _urlResolutionCts;

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

        var presetPath = ActiveVisualizer?.PresetsDirectory;
        var presetPathExists = !string.IsNullOrEmpty(presetPath) && System.IO.Directory.Exists(presetPath);
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] Visualizer presets path: {presetPath} exists? {presetPathExists}");

        var visualizerAvailable = this.VisualizerPlugins.Any(p => p.IsAvailable) && !IsVisualizerDisabled;
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] Visualizer runtime available? {visualizerAvailable} (disabled by switch? {IsVisualizerDisabled})");

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
    // These commands must remain executable while an earlier remote source is
    // connecting. Re-entry lets StartTrackAsync cancel the old attempt and its
    // generation guard ensures only the newest request can own playback state.
    [RelayCommand(AllowConcurrentExecutions = true)]
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

        if (CurrentTrack == null || string.IsNullOrWhiteSpace(CurrentTrack.PlaybackSource))
        {
            if (PlaylistViewModel.PlayQueue.Count > 0)
                CurrentTrack = PlaylistViewModel.PlayQueue[0];
            else
                return;
        }

        await StartTrackAsync();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PlayTrackAsync(JukeboxTrack? track)
    {
        if (track is null)
        {
            return;
        }

        // During the staged UI migration the existing Library grid still calls
        // this command directly. Playback navigation is queue-only, so a saved
        // playlist row must first be mapped to an independent queue copy.
        if (!PlaylistViewModel.PlayQueue.Contains(track))
        {
            int libraryIndex = PlaylistViewModel.LibraryPlaylist.IndexOf(track);
            if (libraryIndex >= 0)
            {
                var queueCopies = PlaylistViewModel.LibraryPlaylist
                    .Select(JukeboxPlaylistViewModel.CopyTrack)
                    .ToList();

                PlaylistViewModel.ReplacePlayQueue(queueCopies);
                track = queueCopies[libraryIndex];
            }
            else
            {
                track = JukeboxPlaylistViewModel.CopyTrack(track);
                PlaylistViewModel.ReplacePlayQueue(new[] { track });
            }
        }

        CurrentTrack = track;
        await StartTrackAsync();
    }

    /// <summary>
    /// Plays one saved-library row without loading the rest of its playlist.
    /// The queue receives an independent copy so subsequent library edits
    /// cannot mutate active playback.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PlaySavedTrackNowAsync(JukeboxTrack? track)
    {
        if (track is null)
        {
            return;
        }

        var queueTrack = JukeboxPlaylistViewModel.CopyTrack(track);
        PlaylistViewModel.ReplacePlayQueue(new[] { queueTrack });
        CurrentTrack = queueTrack;
        await StartTrackAsync();
    }

    [RelayCommand]
    private void QueueSelectedNext(System.Collections.IList? selectedItems)
    {
        var copies = CopySelectedLibraryTracks(selectedItems);
        if (copies.Count == 0)
        {
            return;
        }

        PlaylistViewModel.InsertNextInPlayQueue(copies, CurrentTrack);
    }

    [RelayCommand]
    private void QueueSelectedLast(System.Collections.IList? selectedItems)
    {
        var copies = CopySelectedLibraryTracks(selectedItems);
        if (copies.Count > 0)
        {
            PlaylistViewModel.AppendToPlayQueue(copies);
        }
    }

    private List<JukeboxTrack> CopySelectedLibraryTracks(
        System.Collections.IList? selectedItems)
    {
        if (selectedItems is null)
        {
            return new List<JukeboxTrack>();
        }

        return selectedItems
            .Cast<object>()
            .OfType<JukeboxTrack>()
            .Distinct()
            .OrderBy(track =>
            {
                int index = PlaylistViewModel.LibraryPlaylist.IndexOf(track);
                return index >= 0 ? index : int.MaxValue;
            })
            .Select(JukeboxPlaylistViewModel.CopyTrack)
            .ToList();
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
        Interlocked.Increment(ref _playGeneration);
        _playbackTimer?.Stop();

        // Stop all playback engines unconditionally so no native backend
        // is left running audio/video in the background.
        DetachEngineHandlers(_bassEngine);
        _bassEngine.Stop();

        DetachEngineHandlers(_mpvEngine);
        _mpvEngine.Stop();

        if (VgmEngine != null)
        {
            DetachEngineHandlers(VgmEngine);
            VgmEngine.Stop();
        }

        _activeEngine = null;

        // Cancel any in-flight URL-stream connection attempt — covers the
        // user pressing Stop while a stream is still connecting.
        _streamConnectCts?.Cancel();
        _streamConnectCts?.Dispose();
        _streamConnectCts = null;

        _urlResolutionCts?.Cancel();
        _urlResolutionCts?.Dispose();
        _urlResolutionCts = null;

        _bassEngine.SetStreamCancellationToken(CancellationToken.None);

        IsConnecting = false;
        ConnectingMessage = "";

        CanPlay = PlaylistViewModel.PlayQueue.Count > 0;
        CanPause = false;
        CanStop = false;
        CurrentTimeString = "0:00";
        PlaybackPosition = 0;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PreviousAsync()
    {
        var playlist = GetPlayQueue();
        if (playlist.Count == 0) return;
        var index = CurrentTrack != null ? playlist.IndexOf(CurrentTrack) : -1;

        if (index > 0)
            CurrentTrack = playlist[index - 1];
        else if (IsLoopEnabled)
            CurrentTrack = playlist[^1];
        else
            return;

        await StartTrackAsync();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task NextAsync()
    {
        var playlist = GetPlayQueue();
        if (playlist.Count == 0) return;

        var next = PickNextTrack(IsRandomPlayback);
        if (next == null) { Stop(); return; }

        CurrentTrack = next;
        await StartTrackAsync();
    }
    #endregion

    #region Core Playback
    private async Task StartTrackAsync()
    {
        if (CurrentTrack == null || string.IsNullOrWhiteSpace(CurrentTrack.PlaybackSource)) return;

        var trackToStart = CurrentTrack;
        long playGeneration = Interlocked.Increment(ref _playGeneration);

        _streamConnectCts?.Cancel();
        _streamConnectCts?.Dispose();
        _streamConnectCts = null;

        _urlResolutionCts?.Cancel();
        _urlResolutionCts?.Dispose();
        _urlResolutionCts = null;

        IsConnecting = false;
        ConnectingMessage = "";

        _bassEngine.SetStreamCancellationToken(CancellationToken.None);

        // Stop all engines before opening a new track to prevent overlapping playback.
        DetachEngineHandlers(_bassEngine);
        _bassEngine.Stop();

        DetachEngineHandlers(_mpvEngine);
        _mpvEngine.Stop();

        if (VgmEngine != null)
        {
            DetachEngineHandlers(VgmEngine);
            VgmEngine.Stop();
        }

        _activeEngine = null;

        // Show connection feedback before plugin URL resolution as well as
        // before the media engine opens the resolved URL. Archive.org sources
        // commonly require a resolver request first, so waiting until after
        // resolution would leave the UI looking idle during that network work.
        bool sourceMayBeRemote = trackToStart.PlaybackSource.StartsWith(
            "http://",
            StringComparison.OrdinalIgnoreCase) ||
            trackToStart.PlaybackSource.StartsWith(
                "https://",
                StringComparison.OrdinalIgnoreCase);
        if (sourceMayBeRemote)
        {
            ConnectingMessage = $"Connecting to {ExtractStreamHost(trackToStart.PlaybackSource)}...";
            IsConnecting = true;
        }

        // Resolve stable plugin-owned URLs only after the previous engine has
        // been stopped. OriginalUrl remains unchanged; only FilePath is
        // refreshed for this playback attempt.
        var resolutionCts = new CancellationTokenSource();
        _urlResolutionCts = resolutionCts;
        try
        {
            await ResolveTrackSourceAsync(trackToStart, resolutionCts.Token);
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[Playback] URL resolution cancelled by a newer playback request.");
            return;
        }
        catch (Exception ex)
        {
            if (resolutionCts.IsCancellationRequested ||
                !IsCurrentPlayGeneration(playGeneration) ||
                !ReferenceEquals(CurrentTrack, trackToStart))
            {
                Debug.WriteLine("[Playback] Ignoring resolver failure from a superseded request.");
                return;
            }

            Debug.WriteLine($"[Playback] URL resolution failed: {ex.Message}");
            IsConnecting = false;
            ConnectingMessage = "";
            await _dialogService.ShowErrorAsync(
                "Playback Resolution Error",
                $"Could not prepare '{trackToStart.DisplayName}' for playback.\n\nReason: {UnwrapRootErrorMessage(ex)}");

            if (!IsCurrentPlayGeneration(playGeneration) ||
                !ReferenceEquals(CurrentTrack, trackToStart))
            {
                return;
            }

            CanPlay = PlaylistViewModel.PlayQueue.Count > 0;
            CanPause = false;
            CanStop = false;
            return;
        }
        finally
        {
            if (ReferenceEquals(_urlResolutionCts, resolutionCts))
            {
                _urlResolutionCts = null;
            }

            resolutionCts.Dispose();
        }

        // A newer PlayTrack command may have changed CurrentTrack while an
        // older plugin resolver was completing.
        if (!IsCurrentPlayGeneration(playGeneration) ||
            !ReferenceEquals(CurrentTrack, trackToStart) ||
            string.IsNullOrWhiteSpace(trackToStart.FilePath))
        {
            return;
        }

        string playbackPath = trackToStart.FilePath;
        bool isUrl = playbackPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     playbackPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        if (!isUrl)
        {
            IsConnecting = false;
            ConnectingMessage = "";
        }

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
        // Classify file-backed URL media from the URI path rather than the
        // raw URL. Archive.org (and other providers) may escape path segments
        // or append query/fragment data, which makes a raw EndsWith(".mp4")
        // check miss a video and incorrectly send it to BASS.
        string extensionPath = playbackPath;
        if (Uri.TryCreate(playbackPath, UriKind.Absolute, out var playbackUri))
        {
            extensionPath = Uri.UnescapeDataString(playbackUri.AbsolutePath);
        }
        else
        {
            int suffixIndex = extensionPath.IndexOfAny(new[] { '?', '#' });
            if (suffixIndex >= 0)
            {
                extensionPath = extensionPath[..suffixIndex];
            }
        }

        bool hasVideoExtension = Constants.VideoExtensions.Any(ext =>
            extensionPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        // Browser plugins already provide an authoritative MIME hint through
        // PlayRequest.Codec. The host displays that value in Bitrate, so keep
        // using it when the URL itself is extensionless or redirected.
        bool hasVideoCodec = trackToStart.Bitrate.Contains(
            "video/",
            StringComparison.OrdinalIgnoreCase);
        bool isVideo = hasVideoExtension || hasVideoCodec;

        bool needsMpv = isUrl && (
            playbackPath.Contains(".pls", StringComparison.OrdinalIgnoreCase) ||
            playbackPath.Contains(".ashx", StringComparison.OrdinalIgnoreCase) ||
            isVideo
        );

        bool isAudioExtension = Constants.AudioExtensions.Any(ext =>
            extensionPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        // Route URL streams to BASS (isAudio = true) unless they need MPV.
        // BASS natively handles MP3, AAC, Opus, Shoutcast, and (via basshls)
        // HLS streams — all with visualizations and EQ.
        bool isAudio = isUrl ? !needsMpv : isAudioExtension;

        IsVisualizerVisible = isAudio;

        // Route VGM/VGZ/VGX tracks (local .vgm/.vgz/.vgx files) to the VGM engine if available.
        bool isVgm = playbackPath.EndsWith(".vgz", StringComparison.OrdinalIgnoreCase) ||
                     playbackPath.EndsWith(".vgm", StringComparison.OrdinalIgnoreCase) ||
                     playbackPath.EndsWith(".vgx", StringComparison.OrdinalIgnoreCase);
        Debug.WriteLine($"[Playback] FilePath={playbackPath}");
        Debug.WriteLine($"[Playback] ExtensionPath={extensionPath}, VideoExtension={hasVideoExtension}, VideoCodec={hasVideoCodec}");
        Debug.WriteLine($"[Playback] isVgm={isVgm}, VgmEngine={(VgmEngine != null ? "not null" : "NULL")}");
        Debug.WriteLine($"[Playback] Routing to: {(isVgm && VgmEngine is not null ? "VGM" : (isAudio ? "BASS" : "MPV"))} engine");

        IMediaPlayerEngine engineForAttempt;
        if (isVgm && VgmEngine is not null)
            engineForAttempt = VgmEngine;
        else if (isAudio)
            engineForAttempt = _bassEngine;
        else
            engineForAttempt = _mpvEngine;

        _activeEngine = engineForAttempt;
        engineForAttempt.PlaybackEnded += OnEnginePlaybackEnded;
        engineForAttempt.DurationChanged += OnEngineDurationChanged;
        engineForAttempt.MetadataChanged += OnEngineMetadataChanged;

        // An HTTP(S) source is not automatically a live stream. Archive.org
        // and similar providers expose finite video files through remote URLs;
        // those need the same position polling and seekable range as local
        // video. Keep the live-stream state only for genuinely unbounded
        // remote media (such as radio stations).
        bool isFiniteRemoteMedia = isUrl &&
                                   (isVideo || trackToStart.Length > TimeSpan.Zero);
        IsCurrentTrackStream = isUrl && !isFiniteRemoteMedia;
        if (IsCurrentTrackStream)
        {
            CurrentTimeString = "—";
            TotalTimeString = "—";
            PlaybackPosition = 0;
            PlaybackLength = 0;
        }
        else
        {
            CurrentTimeString = "0:00";
            PlaybackPosition = 0;
            if (trackToStart.Length > TimeSpan.Zero)
            {
                UpdateTrackDuration(trackToStart.Length.TotalMilliseconds);
            }
            else
            {
                CurrentTimeString = "0:00";
                PlaybackLength = 0;
                TotalTimeString = "—";
            }
        }

        engineForAttempt.SetVolume(Volume);

        // ── URL stream connection lifecycle ──
        //
        // For URL streams (radio), PlayAsync only confirms the HTTP
        // connection was opened and BASS accepted the stream handle —
        // actual audio may not flow for another second or two while
        // BASS buffers and detects the codec. We show a "Connecting..."
        // overlay and race PlayAsync + the engine's PlaybackStarted event
        // against a bounded startup timeout. Seekable remote video gets a
        // longer allowance than live audio because MPV may need to follow a
        // CDN redirect and probe a container index before file-loaded.
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
            if (engineForAttempt is BassPlaybackEngine bassForToken)
            {
                bassForToken.SetStreamCancellationToken(connectToken.Value);
            }

            ConnectingMessage = $"Connecting to {ExtractStreamHost(playbackPath)}...";
            IsConnecting = true;
        }

        // One-shot TCS set when the engine raises PlaybackStarted (first
        // PCM buffer for BASS/VGM, first positive time-pos for MPV).
        var playbackStartedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler startedHandler = (_, _) => playbackStartedTcs.TrySetResult(true);
        engineForAttempt.PlaybackStarted += startedHandler;

        Exception? playError = null;
        bool timedOut = false;
        bool cancelled = false;
        int connectionTimeoutMs = isUrl && isVideo
            ? Constants.RemoteVideoConnectionTimeoutMs
            : Constants.StreamConnectionTimeoutMs;
        try
        {
            // PlayAsync itself may throw (HTTP/SSL failure happens inside
            // BassPlaybackEngine.OpenUrlStreamAsync).
            await engineForAttempt.PlayAsync(trackToStart);

            // If our connection token was cancelled while PlayAsync was
            // running (a newer StartTrackAsync superseded us), bail out
            // silently. Don't even reach the timeout wait.
            if (!IsCurrentPlayGeneration(playGeneration) ||
                (connectToken is { } t && t.IsCancellationRequested))
            {
                cancelled = true;
                Debug.WriteLine("[Playback] Connection superseded by a newer StartTrackAsync call (post-PlayAsync).");
            }
            else if (isUrl)
            {
                // PlayAsync returned without throwing — now wait for
                // PlaybackStarted, but cap the wait. We also
                // race against connectToken so a superseding call unblocks
                // us immediately instead of waiting for the timeout.
                Task timeoutTask = Task.Delay(connectionTimeoutMs);
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
                    Debug.WriteLine($"[Playback] Connection timed out after {connectionTimeoutMs}ms without a playback-start signal.");
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
            engineForAttempt.PlaybackStarted -= startedHandler;
        }

        // Clean up the connecting overlay regardless of outcome — it must
        // never get stuck on screen. (If we were superseded, the new call
        // has already set IsConnecting=true with its own message; clearing
        // here is harmless because the new call's set comes after.)
        if (!cancelled && IsCurrentPlayGeneration(playGeneration))
        {
            IsConnecting = false;
            ConnectingMessage = "";
        }

        // If we were cancelled (superseded), bail out silently. Do NOT show
        // an error dialog, do NOT reset transport buttons — the new
        // StartTrackAsync call owns all of that now.
        if (cancelled)
        {
            return;
        }

        if (playError is not null || timedOut)
        {
            if (!IsCurrentPlayGeneration(playGeneration) ||
                !ReferenceEquals(CurrentTrack, trackToStart))
            {
                return;
            }

            // Detach the engine event handlers BEFORE calling Stop(). The
            // BASS End sync fires when StreamFree runs (which Stop calls
            // internally), and if our PlaybackEnded handler is still
            // attached it would dispatch to the UI thread and advance to
            // the next track — wrong behavior after a failed connection.
            DetachEngineHandlers(engineForAttempt);

            // Abort any partial playback state and surface the failure
            // to the user via a single-button (OK) error dialog.
            try { engineForAttempt.Stop(); } catch { /* swallow — best-effort cleanup */ }

            string title = timedOut ? "Connection Unsuccessful" : "Playback Error";
            string reason = timedOut
                ? $"Timed out after {connectionTimeoutMs / 1000} seconds without receiving a playback-start signal."
                : UnwrapRootErrorMessage(playError);

            await _dialogService.ShowErrorAsync(
                title,
                $"Could not play '{trackToStart.DisplayName}':\n{playbackPath}\n\nReason: {reason}");

            if (!IsCurrentPlayGeneration(playGeneration) ||
                !ReferenceEquals(CurrentTrack, trackToStart))
            {
                return;
            }

            // Reset the transport buttons — nothing is playing.
            CanPlay = PlaylistViewModel.PlayQueue.Count > 0;
            CanPause = false;
            CanStop = false;
            return; // do NOT fall through to EQ init / SetPlayingState
        }

        // Success — fall through to EQ init and SetPlayingState below.

        if (!IsCurrentPlayGeneration(playGeneration) ||
            !ReferenceEquals(CurrentTrack, trackToStart))
        {
            return;
        }

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
        if (engineForAttempt is BassPlaybackEngine newBass)
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
        else if (engineForAttempt is VgmPlaybackEngine vgm)
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

    private async Task ResolveTrackSourceAsync(
        JukeboxTrack track,
        CancellationToken cancellationToken)
    {
        string source = track.PlaybackSource;
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        IJukeboxMediaBrowser? resolver = null;

        if (!string.IsNullOrWhiteSpace(track.SourcePluginId))
        {
            var sourcePlugin = PlaylistViewModel.MediaBrowserTabs
                .Select(tab => tab.Browser)
                .FirstOrDefault(browser => string.Equals(
                    browser.Id,
                    track.SourcePluginId,
                    StringComparison.OrdinalIgnoreCase));

            if (sourcePlugin != null && BrowserCanResolve(sourcePlugin, source))
            {
                resolver = sourcePlugin;
            }
        }

        resolver ??= PlaylistViewModel.MediaBrowserTabs
            .Select(tab => tab.Browser)
            .FirstOrDefault(browser => BrowserCanResolve(browser, source));

        if (resolver == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(track.OriginalUrl))
        {
            track.OriginalUrl = source;
        }

        if (string.IsNullOrWhiteSpace(track.SourcePluginId))
        {
            track.SourcePluginId = resolver.Id;
        }

        string resolvedUrl = await resolver.ResolveUrlAsync(source, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(resolvedUrl))
        {
            throw new InvalidOperationException(
                $"Plugin '{resolver.DisplayName}' returned an empty playback URL.");
        }

        track.FilePath = resolvedUrl;
    }

    private static bool BrowserCanResolve(IJukeboxMediaBrowser browser, string source)
    {
        try
        {
            return browser.CanResolve(source);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[Playback] Resolver probe failed for plugin '{browser.Id}': {ex.Message}");
            return false;
        }
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
        if (durationMs > 0)
        {
            IsCurrentTrackStream = false;
        }

        if (IsCurrentTrackStream)
        {
            PlaybackLength = 0;
            TotalTimeString = "—";
        }
        else
        {
            // A remote finite file may briefly report an unavailable duration
            // while the backend is probing it. Preserve the plugin/file
            // metadata range until a backend duration becomes available.
            var knownDuration = CurrentTrack?.Length ?? TimeSpan.Zero;
            if (knownDuration > TimeSpan.Zero)
            {
                PlaybackLength = knownDuration.TotalMilliseconds;
                TotalTimeString = knownDuration.ToString(@"m\:ss");
            }
            else
            {
                PlaybackLength = 0;
                TotalTimeString = "—";
            }
        }
    }

    private void SetPlayingState()
    {
        _playbackTimer?.Start();
        CanPlay = false;
        CanPause = true;
        CanStop = true;
    }

    private bool IsCurrentPlayGeneration(long generation)
        => generation == Volatile.Read(ref _playGeneration);

    private void DetachEngineHandlers(IMediaPlayerEngine engine)
    {
        engine.PlaybackEnded -= OnEnginePlaybackEnded;
        engine.DurationChanged -= OnEngineDurationChanged;
        engine.MetadataChanged -= OnEngineMetadataChanged;

        if (engine is BassPlaybackEngine bass)
        {
            bass.PcmDataAvailable -= OnBassPcmDataAvailable;
        }
        else if (engine is VgmPlaybackEngine vgm)
        {
            vgm.PcmDataAvailable -= OnBassPcmDataAvailable;
        }
    }

    private System.Collections.ObjectModel.ObservableCollection<JukeboxTrack> GetPlayQueue()
        => PlaylistViewModel.PlayQueue;

    private JukeboxTrack? PickNextTrack(bool random)
    {
        var playlist = GetPlayQueue();
        if (playlist.Count == 0) return null;

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

        foreach (var track in PlaylistViewModel.PlayQueue)
            track.IsPlaying = ReferenceEquals(track, value);

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
            bool isStream = IsLiveStreamTrack(value);

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
            bool isStream = IsLiveStreamTrack(CurrentTrack);

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
        long endedGeneration = Volatile.Read(ref _playGeneration);
        var endedTrack = CurrentTrack;

        Debug.WriteLine("[Playback] OnEnginePlaybackEnded fired — dispatching to UI thread.");
        Dispatcher.UIThread.Post(async () =>
        {
            if (!IsCurrentPlayGeneration(endedGeneration) ||
                !ReferenceEquals(CurrentTrack, endedTrack))
            {
                Debug.WriteLine("[Playback] Ignoring stale PlaybackEnded callback.");
                return;
            }

            Debug.WriteLine($"[Playback] PlaybackEnded handler running. IsRepeat={IsRepeatEnabled}, IsRandom={IsRandomPlayback}, QueueCount={PlaylistViewModel.PlayQueue.Count}");

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
        long durationGeneration = Volatile.Read(ref _playGeneration);
        var durationTrack = CurrentTrack;

        Dispatcher.UIThread.Post(() =>
        {
            if (!IsCurrentPlayGeneration(durationGeneration) ||
                !ReferenceEquals(CurrentTrack, durationTrack))
            {
                return;
            }

            UpdateTrackDuration(durationMs);
            if (durationTrack != null && durationMs > 0)
            {
                durationTrack.Length = TimeSpan.FromMilliseconds(durationMs);
            }
        });
    }

    private static bool IsLiveStreamTrack(JukeboxTrack track)
    {
        bool isUrl = track.FilePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     track.FilePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (!isUrl || track.Length > TimeSpan.Zero)
        {
            return false;
        }

        // Plugin MIME hints are authoritative when the remote URL is
        // extensionless or ends in a CDN query string.
        if (track.Bitrate.Contains("video/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string path = track.FilePath;
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            path = Uri.UnescapeDataString(uri.AbsolutePath);
        }

        return !Constants.VideoExtensions.Any(extension =>
            path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
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
            var importedTracks = await PlaylistViewModel.ProcessAndAddFilesAsync(
                new[] { InitialFile },
                PlaylistTarget.PlayQueue,
                NoRecurse);
            if (importedTracks.Count > 0)
            {
                CurrentTrack = importedTracks[0];
                await StartTrackAsync();
            }
        }
    }

    public Task PlayMediaFilesAsync(string[] mediaFiles, bool autoPlay)
        => PlayMediaFilesAsync(mediaFiles, autoPlay, PlaylistTarget.PlayQueue);

    /// <summary>
    /// Imports media for an embedded host. Play Queue is the default through
    /// the two-parameter overload; a host must opt in explicitly to editing
    /// the selected saved playlist.
    /// </summary>
    public async Task PlayMediaFilesAsync(
        string[] mediaFiles,
        bool autoPlay,
        PlaylistTarget target)
    {
        await _backendReadyTcs.Task;
        var importedTracks = await PlaylistViewModel.ProcessAndAddFilesAsync(
            mediaFiles,
            target,
            NoRecurse);
        if (target == PlaylistTarget.PlayQueue &&
            autoPlay &&
            importedTracks.Count > 0)
        {
            CurrentTrack = importedTracks[0];
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
        Interlocked.Increment(ref _playGeneration);

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

            _urlResolutionCts?.Cancel();
            _urlResolutionCts?.Dispose();
            _urlResolutionCts = null;

            if (_activeEngine != null)
            {
                DetachEngineHandlers(_activeEngine);
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
