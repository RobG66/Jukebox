using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jukebox.Models;
using LibVLCSharp.Shared;
using ManagedBass;
using ManagedBass.DirectX8;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Jukebox.ViewModels;

public partial class JukeboxViewModel
{
    #region Fields
    private LibVLC?            _libVLC;
    private int                _playGeneration;
    private readonly SemaphoreSlim _vlcLock = new(1, 1);

    private int              _bassStream          = 0;
    private DSPProcedure?    _bassDspProc;
    private volatile bool    _isAudioMode;
    private bool             _isStoppingExplicitly;
    private string?          _currentBassFilePath;
    private DispatcherTimer? _bassTimer;

    private bool _isUserSeeking    = false;
    private bool _isInternalUpdate = false;

    private readonly HashSet<int> _playedIndices = new();
    private readonly Random       _random        = new();

    #endregion

    #region Observable Properties
    [ObservableProperty] private MediaPlayer? _mediaPlayer;
    [ObservableProperty] private bool         _isVlcReady;
    [ObservableProperty] private bool         _isVisualizerVisible = true;
    [ObservableProperty] private string       _currentTimeString   = "0:00";
    [ObservableProperty] private string       _totalTimeString     = "0:00";
    [ObservableProperty] private double       _playbackLength      = 100;
    [ObservableProperty] private bool         _canPlay             = true;
    [ObservableProperty] private bool         _canPause            = true;
    [ObservableProperty] private bool         _canStop             = true;

    [ObservableProperty]
    private JukeboxTrack? _currentTrack = new() { DisplayName = "GUI Design Mode - No Track Loaded" };

    private double _playbackPosition = 0;
    public double PlaybackPosition
    {
        get => _playbackPosition;
        set
        {
            if (SetProperty(ref _playbackPosition, value) && !_isInternalUpdate)
            {
                if (_bassStream != 0 && Bass.ChannelIsActive(_bassStream) != PlaybackState.Stopped)
                    Bass.ChannelSetPosition(_bassStream, Bass.ChannelSeconds2Bytes(_bassStream, value / 1000.0));
                else if (MediaPlayer != null)
                    MediaPlayer.Time = (long)value;
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
                    Bass.ChannelSetAttribute(_bassStream, ChannelAttribute.Volume, value / 100.0);
                if (MediaPlayer != null)
                    try { MediaPlayer.Volume = (int)value; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"VLC Volume Error: {ex.Message}"); }
            }
        }
    }
    #endregion

    public event EventHandler<short[]>? PcmDataAvailable;

    #region Initialization
    private void InitializePlayback()
    {
        EqViewModel.EqBandUpdated += (_, band) => UpdateBassEqBand(band);
        Bass.Init(-1, 44100, DeviceInitFlags.Default, IntPtr.Zero);
        _bassTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _bassTimer.Tick += BassTimer_Tick;
        _bassTimer.Start();
        _ = InitializeVlcAsync();
    }

    private Task InitializeVlcAsync() => Task.Run(() =>
    {
        try
        {
            Core.Initialize();
            var options = new[]
            {
                "--no-sub-autodetect-file", "--no-video-title-show", "--no-stats",
                "--no-snapshot-preview",    "--no-media-library",    "--no-auto-preparse",
                "--no-plugins-scan",        "--no-lua",              "--no-osd"
            };

            _libVLC = new LibVLC(options);

            var newPlayer = new MediaPlayer(_libVLC);
            newPlayer.TimeChanged     += MediaPlayer_TimeChanged;
            newPlayer.PositionChanged += MediaPlayer_PositionChanged;
            newPlayer.EndReached      += MediaPlayer_EndReached;
            newPlayer.Playing         += MediaPlayer_Playing;
            newPlayer.Paused          += MediaPlayer_Paused;
            newPlayer.Stopped         += MediaPlayer_Stopped;

            Dispatcher.UIThread.Post(() =>
            {
                MediaPlayer        = newPlayer;
                MediaPlayer.Volume = (int)Volume;
                IsVlcReady         = true;
            });
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"LibVLC Initialization Error: {ex.Message}"); }
    });
    #endregion

    #region VLC Event Handlers
    private void MediaPlayer_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        if (_isUserSeeking || _isAudioMode) return;
        Dispatcher.UIThread.Post(() =>
        {
            _isInternalUpdate = true;
            var ts = TimeSpan.FromMilliseconds(e.Time);
            CurrentTimeString = $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
            PlaybackPosition  = e.Time;
            _isInternalUpdate = false;
        });
    }

    private void MediaPlayer_PositionChanged(object? sender, MediaPlayerPositionChangedEventArgs e) { }

    private void MediaPlayer_EndReached(object? sender, EventArgs e)
    {
        int gen = Volatile.Read(ref _playGeneration);
        Dispatcher.UIThread.Post(() => 
        { 
            if (gen == _playGeneration) 
            {
                if (IsRepeatEnabled)
                    PlayInternal(true);
                else
                    Next();
            }
        });
    }

    private void MediaPlayer_Playing(object? sender, EventArgs e)
    {
        if (_isAudioMode) return;
        System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Playing Event Fired");
        Dispatcher.UIThread.Post(async () =>
        {
            CanPlay = false; CanPause = true; CanStop = true;
            await Task.Delay(50);
            if (MediaPlayer != null)
                try { MediaPlayer.Volume = (int)Volume; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"VLC Vol Error: {ex.Message}"); }
            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Playing Event UI Update Done");
        });
    }

    private void MediaPlayer_Paused(object? sender, EventArgs e)
    {
        if (_isAudioMode) return;
        Dispatcher.UIThread.Post(() => { CanPlay = true; CanPause = false; CanStop = true; });
    }

    private void MediaPlayer_Stopped(object? sender, EventArgs e)
    {
        if (_isAudioMode) return;
        Dispatcher.UIThread.Post(() =>
        {
            CanPlay = true; CanPause = false; CanStop = false;
            CurrentTimeString = "0:00";
            PlaybackPosition  = 0;
        });
    }
    #endregion

    #region Bass Helpers
    private void UpdateBassEqBand(EqSliderViewModel band)
    {
        if (band.FxHandle == 0) return;
        Bass.FXSetParameters(band.FxHandle, new DXParamEQParameters
        {
            fBandwidth = 18f,
            fCenter    = band.CenterFrequency,
            fGain      = (float)band.Gain
        });
    }

    private void BassTimer_Tick(object? sender, EventArgs e)
    {
        if (_bassStream == 0) return;

        var state = Bass.ChannelIsActive(_bassStream);
        if (state == PlaybackState.Playing)
        {
            if (_isUserSeeking) return;
            long pos = Bass.ChannelGetPosition(_bassStream);
            double sec = Bass.ChannelBytes2Seconds(_bassStream, pos);
            var ts = TimeSpan.FromSeconds(sec);
            _isInternalUpdate = true;
            CurrentTimeString = $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
            PlaybackPosition  = (long)(sec * 1000);
            _isInternalUpdate = false;
        }
        else if (state == PlaybackState.Stopped && CurrentTrack != null)
        {
            if (IsRepeatEnabled)
                PlayInternal(true);
            else
                Next();
        }
    }

    private void OnBassDsp(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        if (length <= 0) return;
        int count  = length / 2;
        short[] pcm = new short[count];
        
        Marshal.Copy(buffer, pcm, 0, count);
        PcmDataAvailable?.Invoke(this, pcm);
    }
    #endregion

    #region Playback Commands
    [RelayCommand] private void Play() => PlayInternal();

    [RelayCommand]
    private void Pause()
    {
        if (_bassStream != 0 && Bass.ChannelIsActive(_bassStream) == PlaybackState.Playing)
        {
            Bass.ChannelPause(_bassStream);
            CanPlay = true; CanPause = false; CanStop = true;
        }
        else if (IsVlcReady && MediaPlayer?.IsPlaying == true)
        {
            MediaPlayer.Pause();
        }
    }

    [RelayCommand]
    private void Stop()
    {
        Interlocked.Increment(ref _playGeneration);

        if (_bassStream != 0)
        {
            Bass.ChannelStop(_bassStream);
            Bass.StreamFree(_bassStream);
            _bassStream          = 0;
            _currentBassFilePath = null;
        }

        if (IsVlcReady && MediaPlayer != null)
            MediaPlayer.Stop();

        _isStoppingExplicitly = true;
        CurrentTrack          = null;
        _isStoppingExplicitly = false;

        CanPlay = true; CanPause = false; CanStop = false;
        CurrentTimeString = "0:00";
        PlaybackPosition  = 0;
    }

    [RelayCommand]
    private void Previous()
    {
        if (PlaylistViewModel.Playlist.Count == 0) return;
        var index    = CurrentTrack != null ? PlaylistViewModel.Playlist.IndexOf(CurrentTrack) : -1;
        var oldTrack = CurrentTrack;

        if (index > 0)
            CurrentTrack = PlaylistViewModel.Playlist[index - 1];
        else if (IsLoopEnabled)
            CurrentTrack = PlaylistViewModel.Playlist[^1];
        else
            return;

        PlayInternal(CurrentTrack == oldTrack);
    }

    [RelayCommand]
    private void Next()
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

        PlayInternal(CurrentTrack == oldTrack);
    }
    #endregion

    #region Track Changed
    partial void OnCurrentTrackChanged(JukeboxTrack? value)
    {
        if (value == null)
        {
            if (!_isStoppingExplicitly) MediaPlayer?.Stop();
            return;
        }

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
    private void PlayInternal(bool forceRestart = false)
    {
        if (CurrentTrack == null || string.IsNullOrEmpty(CurrentTrack.FilePath))
        {
            if (PlaylistViewModel.Playlist.Count > 0)
                CurrentTrack = PlaylistViewModel.Playlist[0];
            else
                return;
        }

        int generation = Interlocked.Increment(ref _playGeneration);
        bool isAudio   = Constants.AudioExtensions.Any(ext =>
            CurrentTrack.FilePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        _isAudioMode         = isAudio;
        IsVisualizerVisible  = isAudio || ForceVisualizer;

        if (isAudio)
        {
            bool needNewStream = forceRestart
                || _bassStream == 0
                || !string.Equals(_currentBassFilePath, CurrentTrack.FilePath, StringComparison.OrdinalIgnoreCase);

            if (needNewStream)
            {
                // Idle VLC while BASS takes over
                if (IsVlcReady && MediaPlayer != null)
                {
                    var oldMedia = MediaPlayer.Media;
                    MediaPlayer.Media = null;
                    _ = Task.Run(async () =>
                    {
                        await _vlcLock.WaitAsync();
                        try
                        {
                            try { MediaPlayer?.Stop();     } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"VLC Stop Error: {ex.Message}"); }
                            try { oldMedia?.Dispose();     } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"VLC Dispose Error: {ex.Message}"); }
                        }
                        finally { _vlcLock.Release(); }
                    });
                }

                if (_bassStream != 0) { Bass.StreamFree(_bassStream); _bassStream = 0; }

                _bassStream          = Bass.CreateStream(CurrentTrack.FilePath, 0, 0, BassFlags.Default);
                _currentBassFilePath = CurrentTrack.FilePath;

                if (_bassStream != 0)
                {
                    foreach (var band in EqViewModel.EqBands)
                    {
                        band.FxHandle = Bass.ChannelSetFX(_bassStream, EffectType.DXParamEQ, 0);
                        UpdateBassEqBand(band);
                    }

                    _bassDspProc = new DSPProcedure(OnBassDsp);
                    Bass.ChannelSetDSP(_bassStream, _bassDspProc);
                    Bass.ChannelSetAttribute(_bassStream, ChannelAttribute.Volume, Volume / 100.0);
                    Bass.ChannelPlay(_bassStream);

                    CanPlay = false; CanPause = true; CanStop = true;

                    long len    = Bass.ChannelGetLength(_bassStream);
                    double sec  = Bass.ChannelBytes2Seconds(_bassStream, len);
                    PlaybackLength = (long)(sec * 1000);
                    var ts = TimeSpan.FromSeconds(sec);
                    TotalTimeString = $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
                }
                return;
            }

            // Resume paused Bass stream
            if (_bassStream != 0 && Bass.ChannelIsActive(_bassStream) == PlaybackState.Paused)
                Bass.ChannelPlay(_bassStream);

            CanPlay = false; CanPause = true; CanStop = true;
        }
        else
        {
            if (!IsVlcReady || MediaPlayer == null || _libVLC == null) return;

            var targetUri = new Uri(CurrentTrack.FilePath).AbsoluteUri;
            if (forceRestart || MediaPlayer.Media == null || MediaPlayer.Media.Mrl != targetUri)
            {
                var oldMedia      = MediaPlayer.Media;
                MediaPlayer.Media = new Media(_libVLC, CurrentTrack.FilePath, FromType.FromPath);

                if (_bassStream != 0) { Bass.StreamFree(_bassStream); _bassStream = 0; _currentBassFilePath = null; }

                _ = Task.Run(async () =>
                {
                    System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] VLC Task Started");
                    await _vlcLock.WaitAsync();
                    try
                    {
                        System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Calling Stop()");
                        try { MediaPlayer?.Stop();  } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"VLC Stop Error: {ex.Message}"); }
                        System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Calling Dispose()");
                        try { oldMedia?.Dispose();  } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"VLC Dispose Error: {ex.Message}"); }
                        System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Calling Play()");
                        if (Volatile.Read(ref _playGeneration) == generation)
                            try { MediaPlayer?.Play(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"VLC Play Error: {ex.Message}"); }
                        System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Play() returned");
                    }
                    finally { _vlcLock.Release(); }
                });
                return;
            }

            if (!MediaPlayer.IsPlaying) MediaPlayer.Play();
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
                PlayInternal();
            }
        }
    }

    public async Task PlayMediaFilesAsync(string[] mediaFiles, bool autoPlay)
    {
        await PlaylistViewModel.ProcessAndAddFilesAsync(mediaFiles.ToList(), NoRecurse);
        if (autoPlay && PlaylistViewModel.Playlist.Count > 0)
        {
            CurrentTrack = PlaylistViewModel.Playlist[0];
            PlayInternal();
        }
    }

    public void LoadSystemLogo(string systemName) { /* placeholder */ }
    #endregion

    #region Dispose
    private void DisposePlayback()
    {
        if (_bassTimer != null)
        {
            _bassTimer.Tick -= BassTimer_Tick;
            _bassTimer.Stop();
            _bassTimer = null;
        }

        if (_bassStream != 0)
        {
            Bass.StreamFree(_bassStream);
            _bassStream = 0;
        }
        Bass.Free();

        var player = MediaPlayer;
        var media  = MediaPlayer?.Media;
        var libVlc = _libVLC;
        MediaPlayer = null;

        _ = Task.Run(async () =>
        {
            await _vlcLock.WaitAsync();
            try
            {
                try { player?.Stop();    } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"VLC Dispose Stop Error: {ex.Message}"); }
                try { media?.Dispose();  } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"VLC Dispose Media Error: {ex.Message}"); }
                try { player?.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"VLC Dispose Player Error: {ex.Message}"); }
                try { libVlc?.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"VLC Dispose Lib Error: {ex.Message}"); }
            }
            finally { _vlcLock.Release(); }
        });
    }
    #endregion
}
