using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jukebox.Extensions;
using Jukebox.Models;
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

    // REFACTOR: tracks the currently-subscribed track so we can unsubscribe
    // the CORRECT handler on track change (was smell §4.2 Critical:
    // OnCurrentTrackChanged leaks event handler — the original code
    // unsubscribed _lastTrack but if the same track was set twice it would
    // double-subscribe).
    private JukeboxTrack? _subscribedTrack;
    private bool _isPlaybackDisposed;

    // REFACTOR: lock object protecting _bassStream access between
    // PlaybackTimer_Tick and DisposeBass (was smell §4.2 Critical: race
    // condition between PlaybackTimer_Tick and Dispose).
    private readonly object _bassStreamLock = new();
    #endregion

    #region Observable Properties
    [ObservableProperty] private bool _isBackendReady;
    [ObservableProperty] private bool _isInitializing;

    // REFACTOR: IsVisualizerVisible retains its original semantic of
    // "audio mode is active" (i.e. BASS is the active backend, MPV is
    // not). It does NOT necessarily mean the ProjectM control is on
    // screen — that requires BOTH IsVisualizerVisible=true AND
    // IsVisualizerAvailable=true (the latter probes for the optional
    // ProjectM drop-in at runtime). When audio is playing but
    // IsVisualizerAvailable is false, BASS plays audio normally and the
    // MediaHost is left empty (pure audio mode, no ProjectM dependency).
    [ObservableProperty] private bool _isVisualizerVisible = true;
    [ObservableProperty] private string _currentTimeString = "0:00";
    [ObservableProperty] private string _totalTimeString = "0:00";
    [ObservableProperty] private double _playbackLength = 100;
    [ObservableProperty] private bool _canPlay = false;
    [ObservableProperty] private bool _canPause = false;
    [ObservableProperty] private bool _canStop = false;
    [ObservableProperty] private bool _isSeeking = false;

    [ObservableProperty]
    private JukeboxTrack? _currentTrack = new() { DisplayName = "No Track Loaded" };

    private double _playbackPosition = 0;
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
                ApplyBassVolume(value);
                ApplyMpvVolume(value);
            }
        }
    }
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
            // REFACTOR: magic number 250 → named constant (smell §4.2, §6.4).
            Interval = TimeSpan.FromMilliseconds(Constants.PlaybackTimerIntervalMs)
        };
        _playbackTimer.Tick += PlaybackTimer_Tick;

        await Task.Run(() =>
        {
            InitializeBass();
            InitializeMpv();
        });

        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] Backend Initialization completed in {sw.ElapsedMilliseconds}ms overall.");

        // Diagnostic: log the ProjectM presets folder presence (the
        // visualizer runtime uses this as its availability check).
        var projectMPath = Jukebox.Services.PathProvider.Current.ProjectMPresetsDirectory;
        var projectMExists = System.IO.Directory.Exists(projectMPath);
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] ProjectM presets path: {projectMPath} exists? {projectMExists}");

        // Probe the optional visualizer runtime (ProjectM + JukeboxVisualizations.dll).
        // The probe is cached after the first call; we expose the result as
        // IsVisualizerAvailable so the transport-bar button can hide itself
        // when the drop-in is absent.
        var visualizerAvailable = this.VisualizerRuntime.IsAvailable;
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] Visualizer runtime available? {visualizerAvailable}");

        Dispatcher.UIThread.Post(() =>
        {
            IsVisualizerAvailable = visualizerAvailable;
            IsBackendReady = true;
            IsInitializing = false;
            _backendReadyTcs.TrySetResult();
            // REFACTOR: SafeFireAndForget instead of `_ = InitializeStartupAsync()`
            // (was smell §4.2 Critical: fire-and-forget InitializeStartupAsync).
            InitializeStartupAsync().SafeFireAndForget(nameof(InitializeStartupAsync));
        });
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (IsSeeking) return;

        _isTimerUpdating = true;
        try
        {
            // REFACTOR: lock around native handle read to prevent race with
            // DisposeBass (was smell §4.2 Critical: race condition between
            // PlaybackTimer_Tick and Dispose).
            double positionMs;
            lock (_bassStreamLock)
            {
                positionMs = IsVisualizerVisible
                    ? GetBassPositionMs()
                    : GetMpvPositionMs();
            }

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

        // Resume from pause
        if (CanStop && CurrentTrack != null)
        {
            if (IsVisualizerVisible) ResumeBass();
            else ResumeMpv();

            _playbackTimer?.Start();
            CanPlay = false;
            CanPause = true;
            return;
        }

        // Start fresh — pick first track if none set
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
        if (IsVisualizerVisible) PauseBass();
        else PauseMpv();

        _playbackTimer?.Stop();
        CanPlay = true;
        CanPause = false;
        CanStop = true;
    }

    [RelayCommand]
    private void Stop()
    {
        _playbackTimer?.Stop();

        if (IsVisualizerVisible) StopBass();
        else StopMpv();

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
    private async Task StartTrackAsync(bool mpvEndReached = false)
    {
        if (CurrentTrack == null || string.IsNullOrEmpty(CurrentTrack.FilePath)) return;

        bool isAudio = Constants.AudioExtensions.Any(ext =>
            CurrentTrack.FilePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        if (IsVisualizerVisible) StopBass();
        else StopMpv();

        IsVisualizerVisible = isAudio;

        if (isAudio)
            await PlayAudioAsync();
        else
            await PlayVideoAsync();
    }

    private void SeekToPosition(double positionMs)
    {
        if (IsVisualizerVisible) SeekBass(positionMs);
        else SeekMpv(positionMs);
    }

    private void UpdateTrackDuration(double durationMs)
    {
        PlaybackLength = durationMs;
        TotalTimeString = TimeSpan.FromMilliseconds(durationMs).ToString(@"m\:ss");
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
        // REFACTOR: use ReferenceEquals guard before unsubscribing to prevent
        // double-subscription when the same track is set twice (was smell
        // §4.2 Critical: OnCurrentTrackChanged leaks event handler).
        if (_subscribedTrack != null && !ReferenceEquals(_subscribedTrack, value))
            _subscribedTrack.PropertyChanged -= CurrentTrack_PropertyChanged;

        _subscribedTrack = value;
        if (value == null) return;

        value.PropertyChanged += CurrentTrack_PropertyChanged;

        if (IsShowPlayingEnabled)
        {
            // REFACTOR: OSD animation delegated to IShowPlayingService
            // (was smell §4.1 Warning: Direct dispatcher coupling in OSD animation).
            _showPlayingService.ShowAsync(value.DisplayName)
                .SafeFireAndForget(nameof(_showPlayingService.ShowAsync));
        }

        if (!string.IsNullOrEmpty(value.FilePath))
        {
            PlaybackLength = value.Length.TotalMilliseconds;
            TotalTimeString = value.DisplayLength;
        }
    }

    private void CurrentTrack_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JukeboxTrack.Length) && CurrentTrack != null)
        {
            PlaybackLength = CurrentTrack.Length.TotalMilliseconds;
            TotalTimeString = CurrentTrack.DisplayLength;
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
        // REFACTOR: was `/* placeholder */` empty method — smell §4.2 Minor.
        // Either implement or remove; for now, log a warning so callers know
        // the operation is a no-op.
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
            // REFACTOR: unsubscribe the correct track (was _lastTrack, which
            // could be stale if CurrentTrack changed mid-dispose).
            if (_subscribedTrack != null)
                _subscribedTrack.PropertyChanged -= CurrentTrack_PropertyChanged;

            EqViewModel.EqBandUpdated -= OnEqBandUpdated;

            // Stop the timer BEFORE disposing native handles — eliminates the
            // race window between PlaybackTimer_Tick and DisposeBass (smell §4.2).
            _playbackTimer?.Stop();

            await DisposeMpvAsync();

            // Lock around Bass dispose to serialize with any in-flight Tick.
            lock (_bassStreamLock)
            {
                DisposeBass();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Dispose] Error cleaning up playback backend: {ex.Message}");
        }
    }
    #endregion
}
