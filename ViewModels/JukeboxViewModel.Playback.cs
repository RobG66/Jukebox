using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jukebox.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
    private JukeboxTrack? _lastTrack;
    private bool _isPlaybackDisposed;
    #endregion

    #region Observable Properties
    [ObservableProperty] private bool _isBackendReady;
    [ObservableProperty] private bool _isInitializing;
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
                ApplyVlcVolume(value);
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

        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _playbackTimer.Tick += PlaybackTimer_Tick;

        await Task.Run(() =>
        {
            InitializeBass();
            InitializeVlc();
        });

        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] Backend Initialization completed in {sw.ElapsedMilliseconds}ms overall.");

        var projectMPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectM", "Presets");
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] ProjectM Path Check: {projectMPath} exists? {System.IO.Directory.Exists(projectMPath)}");

        Dispatcher.UIThread.Post(() =>
        {
            IsBackendReady = true;
            IsInitializing = false;
            _backendReadyTcs.TrySetResult();
            _ = InitializeStartupAsync();
        });
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (IsSeeking) return;

        _isTimerUpdating = true;
        try
        {
            double positionMs = IsVisualizerVisible
                ? GetBassPositionMs()
                : GetVlcPositionMs();

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
            else ResumeVlc();

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
        else PauseVlc();

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
        else StopVlc();

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
    private async Task StartTrackAsync(bool vlcEndReached = false)
    {
        if (CurrentTrack == null || string.IsNullOrEmpty(CurrentTrack.FilePath)) return;

        bool isAudio = Constants.AudioExtensions.Any(ext =>
            CurrentTrack.FilePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        if (IsVisualizerVisible) StopBass();
        else StopVlc(vlcEndReached);

        IsVisualizerVisible = isAudio;

        if (isAudio)
            await PlayAudioAsync();
        else
            await PlayVideoAsync();
    }

    private void SeekToPosition(double positionMs)
    {
        if (IsVisualizerVisible) SeekBass(positionMs);
        else SeekVlc(positionMs);
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
        if (_lastTrack != null)
            _lastTrack.PropertyChanged -= CurrentTrack_PropertyChanged;

        _lastTrack = value;
        if (value == null) return;

        value.PropertyChanged += CurrentTrack_PropertyChanged;

        if (IsShowPlayingEnabled)
        {
            ShowPlayingText = value.DisplayName;
            _ = TriggerShowPlayingOSDAsync();
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

    public void LoadSystemLogo(string systemName) { /* placeholder */ }
    #endregion

    #region Dispose
    public async Task DisposePlaybackAsync()
    {
        if (_isPlaybackDisposed) return;
        _isPlaybackDisposed = true;

        try
        {
            if (_lastTrack != null)
                _lastTrack.PropertyChanged -= CurrentTrack_PropertyChanged;

            EqViewModel.EqBandUpdated -= OnEqBandUpdated;
            _playbackTimer?.Stop();

            await DisposeVlcAsync();
            DisposeBass();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Dispose] Error cleaning up playback backend: {ex.Message}");
        }
    }
    #endregion
}
