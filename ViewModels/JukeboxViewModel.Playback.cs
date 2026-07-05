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

    public event EventHandler<short[]>? PcmDataAvailable;
    #endregion

    #region Observable Properties
    [ObservableProperty] private bool _isBackendReady;
    [ObservableProperty] private bool _isInitializing;
    [ObservableProperty] private bool _isBassAvailable;
    [ObservableProperty] private bool _isMpvAvailable;
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
        if (IsSeeking || _activeEngine == null) return;

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

        if (_activeEngine != null)
        {
            _activeEngine.Stop();
            _activeEngine.PlaybackEnded -= OnEnginePlaybackEnded;
            _activeEngine.DurationChanged -= OnEngineDurationChanged;
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

        bool isAudio = isUrl || Constants.AudioExtensions.Any(ext =>
            CurrentTrack.FilePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

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

        _activeEngine.SetVolume(Volume);

        await _activeEngine.PlayAsync(CurrentTrack);

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
        if (_subscribedTrack != null && !ReferenceEquals(_subscribedTrack, value))
            _subscribedTrack.PropertyChanged -= CurrentTrack_PropertyChanged;

        _subscribedTrack = value;

        foreach (var track in PlaylistViewModel.Playlist)
        {
            track.IsPlaying = (track == value);
        }

        if (value == null) return;

        value.PropertyChanged += CurrentTrack_PropertyChanged;

        if (IsShowPlayingEnabled)
        {
            _showPlayingService.ShowAsync(value.DisplayName, ShowPlayingTimeout)
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

             if (_activeEngine != null)
            {
                _activeEngine.PlaybackEnded -= OnEnginePlaybackEnded;
                _activeEngine.DurationChanged -= OnEngineDurationChanged;
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
