using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jukebox.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using LibVLCSharp.Shared;
using ManagedBass;
using ManagedBass.DirectX8;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Jukebox.ViewModels;

public partial class JukeboxViewModel
{
    #region Fields
    private LibVLC? _libVLC;
    private readonly HashSet<int> _playedIndices = new();
    private readonly Random _random = new();
    private int _bassStream = 0;
    private DispatcherTimer? _playbackTimer;
    private readonly int[] _eqFxHandles = new int[10];
    private DSPProcedure? _dspProcedure;
    private SyncProcedure? _endSyncProcedure;
    private int _dspHandle;
    private int _endSyncHandle;
    private bool _isTimerUpdating;

    public event EventHandler<short[]>? PcmDataAvailable;
    #endregion

    #region Observable Properties
    [ObservableProperty] private MediaPlayer? _mediaPlayer;
    [ObservableProperty] private bool _isBassAvailable;
    [ObservableProperty] private bool _isVlcAvailable;
    [ObservableProperty] private bool _isBackendReady;
    [ObservableProperty] private bool _isInitializing;
    [ObservableProperty] private bool _isVisualizerVisible = true;
    [ObservableProperty] private string _currentTimeString = "0:00";
    [ObservableProperty] private string _totalTimeString = "0:00";
    [ObservableProperty] private double _playbackLength = 100;
    [ObservableProperty] private bool _canPlay = false;
    [ObservableProperty] private bool _canPause = false;
    [ObservableProperty] private bool _canStop = false;

    [ObservableProperty]
    private JukeboxTrack? _currentTrack = new() { DisplayName = "No Track Loaded" };

    private double _playbackPosition = 0;
    public double PlaybackPosition
    {
        get => _playbackPosition;
        set
        {
            if (SetProperty(ref _playbackPosition, value) && !_isTimerUpdating)
            {
                // User is seeking manually
                if (IsVisualizerVisible && _bassStream != 0)
                {
                    Bass.ChannelSetPosition(_bassStream, Bass.ChannelSeconds2Bytes(_bassStream, value / 1000.0));
                }
                else if (!IsVisualizerVisible && MediaPlayer != null)
                {
                    MediaPlayer.Time = (long)value;
                }
            }
        }
    }

    private double _volume = 100;
    public double Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, value))
            {
                if (_bassStream != 0)
                {
                    Bass.ChannelSetAttribute(_bassStream, ChannelAttribute.Volume, value / 100.0);
                }
                if (MediaPlayer != null)
                {
                    MediaPlayer.Volume = (int)value;
                }
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

        // Setup EqBandUpdated listener
        EqViewModel.EqBandUpdated += OnEqBandUpdated;

        // Setup Playback Timer
        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _playbackTimer.Tick += PlaybackTimer_Tick;

        await Task.Run(() =>
        {
            // BASS Initialization
            var bassSw = Stopwatch.StartNew();
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] Initializing ManagedBass...");
            try
            {
                bool bassOk = Bass.Init(-1, 44100, DeviceInitFlags.Default, IntPtr.Zero);
                if (bassOk)
                {
                    Dispatcher.UIThread.Post(() => IsBassAvailable = true);
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] ManagedBass initialized successfully in {bassSw.ElapsedMilliseconds}ms.");
                }
                else
                {
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] ManagedBass failed to initialize. Error: {Bass.LastError}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] ManagedBass Init Exception: {ex.Message}");
            }

            // LibVLC Initialization
            var vlcSw = Stopwatch.StartNew();
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] Initializing LibVLC...");
            try
            {
                Core.Initialize();
                _libVLC = new LibVLC();
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] new LibVLC() complete.");
                Dispatcher.UIThread.Post(() => IsVlcAvailable = true);
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] LibVLC initialized successfully in {vlcSw.ElapsedMilliseconds}ms.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] LibVLC Init Exception: {ex.Message}");
            }
        });

        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] Backend Initialization completed in {sw.ElapsedMilliseconds}ms overall.");

        var projectMPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectM", "Presets");
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] ProjectM Path Check: {projectMPath} exists? {System.IO.Directory.Exists(projectMPath)}");

        Dispatcher.UIThread.Post(() =>
        {
            IsBackendReady = true;
            IsInitializing = false;
            _ = InitializeStartupAsync();
        });
    }

    private void OnEqBandUpdated(object? sender, EqSliderViewModel band)
    {
        if (_bassStream != 0)
        {
            int index = EqViewModel.EqBands.IndexOf(band);
            if (index >= 0 && index < 10 && _eqFxHandles[index] != 0)
            {
                var p = new DXParamEQParameters
                {
                    fBandwidth = 18f,
                    fCenter = band.CenterFrequency,
                    fGain = (float)band.Gain
                };
                Bass.FXSetParameters(_eqFxHandles[index], p);
            }
        }
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        _isTimerUpdating = true;
        try
        {
            if (IsVisualizerVisible) // Audio
            {
                if (_bassStream != 0)
                {
                    var pos = Bass.ChannelGetPosition(_bassStream);
                    PlaybackPosition = TimeSpan.FromSeconds(Bass.ChannelBytes2Seconds(_bassStream, pos)).TotalMilliseconds;
                    CurrentTimeString = TimeSpan.FromMilliseconds(PlaybackPosition).ToString(@"m\:ss");
                }
            }
            else // Video
            {
                if (MediaPlayer != null)
                {
                    PlaybackPosition = MediaPlayer.Time; // Time is in ms
                    CurrentTimeString = TimeSpan.FromMilliseconds(PlaybackPosition).ToString(@"m\:ss");
                }
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

        if (CanStop && CurrentTrack != null) // It was paused
        {
            if (IsVisualizerVisible && _bassStream != 0)
            {
                Bass.ChannelPlay(_bassStream);
            }
            else if (!IsVisualizerVisible && MediaPlayer != null)
            {
                MediaPlayer.Play();
            }

            _playbackTimer?.Start();
            CanPlay = false;
            CanPause = true;
            CanStop = true;
        }
        else // Starting fresh
        {
            await PlayInternalAsync();
        }
    }

    [RelayCommand]
    private async Task PlayTrackAsync(JukeboxTrack track)
    {
        CurrentTrack = track;
        await PlayInternalAsync();
    }

    [RelayCommand]
    private void Pause()
    {
        if (IsVisualizerVisible && _bassStream != 0)
        {
            Bass.ChannelPause(_bassStream);
        }
        else if (!IsVisualizerVisible && MediaPlayer != null)
        {
            MediaPlayer.Pause();
        }

        _playbackTimer?.Stop();
        CanPlay = true;
        CanPause = false;
        CanStop = true;
    }

    private void StopEngines()
    {
        if (_bassStream != 0)
        {
            if (_dspHandle != 0)
            {
                Bass.ChannelRemoveDSP(_bassStream, _dspHandle);
                _dspHandle = 0;
            }
            if (_endSyncHandle != 0)
            {
                Bass.ChannelRemoveSync(_bassStream, _endSyncHandle);
                _endSyncHandle = 0;
            }
            Bass.StreamFree(_bassStream);
            _bassStream = 0;
            Array.Clear(_eqFxHandles, 0, _eqFxHandles.Length);
        }

        if (MediaPlayer != null)
        {
            MediaPlayer.Stop();
            MediaPlayer.Media?.Dispose();
            MediaPlayer.Media = null;
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _playbackTimer?.Stop();
        StopEngines();

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
        var oldTrack = CurrentTrack;

        if (index > 0)
            CurrentTrack = PlaylistViewModel.Playlist[index - 1];
        else if (IsLoopEnabled)
            CurrentTrack = PlaylistViewModel.Playlist[^1];
        else
            return;

        await PlayInternalAsync();
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (PlaylistViewModel.Playlist.Count == 0) return;
        var oldTrack = CurrentTrack;

        if (IsRandomPlayback)
        {
            if (_playedIndices.Count >= PlaylistViewModel.Playlist.Count)
            {
                if (IsLoopEnabled) _playedIndices.Clear();
                else return;
            }
            int nextIndex;
            do { nextIndex = _random.Next(PlaylistViewModel.Playlist.Count); }
            while (_playedIndices.Contains(nextIndex) && _playedIndices.Count < PlaylistViewModel.Playlist.Count);
            _playedIndices.Add(nextIndex);
            CurrentTrack = PlaylistViewModel.Playlist[nextIndex];
        }
        else
        {
            var index = CurrentTrack != null ? PlaylistViewModel.Playlist.IndexOf(CurrentTrack) : -1;
            if (index >= 0 && index < PlaylistViewModel.Playlist.Count - 1)
                CurrentTrack = PlaylistViewModel.Playlist[index + 1];
            else if (IsLoopEnabled)
                CurrentTrack = PlaylistViewModel.Playlist[0];
            else
                return;
        }

        await PlayInternalAsync();
    }
    #endregion

    #region Track Changed
    partial void OnCurrentTrackChanged(JukeboxTrack? value)
    {
        if (value == null) return;

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
    #endregion

    #region Core Playback
    private async Task PlayInternalAsync()
    {
        if (CurrentTrack == null || string.IsNullOrEmpty(CurrentTrack.FilePath))
        {
            if (PlaylistViewModel.Playlist.Count > 0)
                CurrentTrack = PlaylistViewModel.Playlist[0];
            else
                return;
        }

        bool isAudio = Constants.AudioExtensions.Any(ext =>
            CurrentTrack.FilePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        StopEngines();

        IsVisualizerVisible = isAudio;

        if (isAudio)
        {
            await PlayAudioAsync();
        }
        else
        {
            await PlayVideoAsync();
        }
    }

    private async Task PlayAudioAsync()
    {
        if (!IsBassAvailable)
        {
            await Jukebox.Views.ThreeButtonDialogView.ShowErrorAsync(
                "Audio Error",
                "Audio playback is unavailable. ManagedBass failed to initialize.");
            return;
        }

        if (CurrentTrack == null) return;

        _bassStream = Bass.CreateStream(CurrentTrack.FilePath, 0, 0, BassFlags.Default);
        if (_bassStream != 0)
        {
            // Set Volume
            Bass.ChannelSetAttribute(_bassStream, ChannelAttribute.Volume, Volume / 100.0);

            // Setup EQ
            for (int i = 0; i < 10; i++)
            {
                if (EqViewModel.EqBands.Count > i)
                {
                    _eqFxHandles[i] = Bass.ChannelSetFX(_bassStream, EffectType.DXParamEQ, 0);
                    var p = new DXParamEQParameters
                    {
                        fBandwidth = 18f,
                        fCenter = EqViewModel.EqBands[i].CenterFrequency,
                        fGain = (float)EqViewModel.EqBands[i].Gain
                    };
                    Bass.FXSetParameters(_eqFxHandles[i], p);
                }
            }

            _dspProcedure = new DSPProcedure(OnDsp);
            _dspHandle = Bass.ChannelSetDSP(_bassStream, _dspProcedure, IntPtr.Zero, 0);

            _endSyncProcedure = new SyncProcedure((handle, channel, data, user) =>
            {
                Dispatcher.UIThread.Post(() => NextCommand.Execute(null));
            });
            _endSyncHandle = Bass.ChannelSetSync(_bassStream, SyncFlags.End, 0, _endSyncProcedure, IntPtr.Zero);

            Bass.ChannelPlay(_bassStream);
            SetPlayingState();
        }
    }

    private void OnDsp(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        if (length > 0 && PcmDataAvailable != null)
        {
            int count = length / 2;
            short[] pcm = new short[count];
            System.Runtime.InteropServices.Marshal.Copy(buffer, pcm, 0, count);
            PcmDataAvailable.Invoke(this, pcm);
        }
    }

    private async Task PlayVideoAsync()
    {
        if (!IsVlcAvailable)
        {
            await Jukebox.Views.ThreeButtonDialogView.ShowErrorAsync(
                "Video Error",
                "Video playback is unavailable. LibVLC failed to initialize.");
            return;
        }

        if (CurrentTrack == null || _libVLC == null) return;

        if (MediaPlayer == null)
        {
            MediaPlayer = new MediaPlayer(_libVLC);
            MediaPlayer.EndReached += (sender, e) =>
            {
                // LibVLC events run on a background thread
                Dispatcher.UIThread.Post(() => NextCommand.Execute(null));
            };
        }

        var media = new Media(_libVLC, new Uri(CurrentTrack.FilePath));
        MediaPlayer.Media = media;
        MediaPlayer.Volume = (int)Volume;
        MediaPlayer.Play();

        SetPlayingState();
    }

    private void SetPlayingState()
    {
        _playbackTimer?.Start();
        CanPlay = false;
        CanPause = true;
        CanStop = true;
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
                await PlayInternalAsync();
            }
        }
    }

    public async Task PlayMediaFilesAsync(string[] mediaFiles, bool autoPlay)
    {
        await PlaylistViewModel.ProcessAndAddFilesAsync(mediaFiles.ToList(), NoRecurse);
        if (autoPlay && PlaylistViewModel.Playlist.Count > 0)
        {
            CurrentTrack = PlaylistViewModel.Playlist[0];
            await PlayInternalAsync();
        }
    }

    public void LoadSystemLogo(string systemName) { /* placeholder */ }
    #endregion

    #region Dispose
    public void DisposePlayback()
    {
        try
        {
            EqViewModel.EqBandUpdated -= OnEqBandUpdated;
            _playbackTimer?.Stop();

            if (MediaPlayer != null)
            {
                if (MediaPlayer.IsPlaying)
                {
                    MediaPlayer.Stop();
                }
                MediaPlayer.Media?.Dispose();
                MediaPlayer.Dispose();
                MediaPlayer = null;
            }

            if (_libVLC != null)
            {
                _libVLC.Dispose();
                _libVLC = null;
            }

            if (IsBassAvailable)
            {
                if (_bassStream != 0)
                {
                    Bass.StreamFree(_bassStream);
                }
                Bass.Free();
                IsBassAvailable = false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Dispose] Error cleaning up playback backend: {ex.Message}");
        }
    }
    #endregion
}
