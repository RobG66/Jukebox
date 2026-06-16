using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Selection;
using ManagedBass;
using ManagedBass.DirectX8;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Jukebox.ViewModels;

public class JukeboxTrack
{
    public string DisplayName { get; set; } = "Unknown Track";
    public string Length { get; set; } = "0:00";
    public string Bitrate { get; set; } = "128 kbps";
    public string FilePath { get; set; } = string.Empty;
    public bool IsSelected { get; set; }
}



public partial class JukeboxViewModel : ViewModelBase, IDisposable
{
    #region Public Properties
    public int InitialVolume { get; set; } = 100;
    public string? InitialFile { get; set; }
    public bool ForceVisualizer { get; set; }
    public bool NoRecurse { get; set; }
    public bool StayOnTop { get; set; }
    public bool IsAudioOnly { get; set; }
    public bool IsVideoOnly { get; set; }
    [ObservableProperty] private bool _isShowPlayingEnabled;
    public int ShowPlayingTimeout { get; set; } = 10;
    #endregion

    #region Fields & Constants
    private LibVLC? _libVLC;
    private CancellationTokenSource? _showPlayingCts;
    private List<int> _playedIndices = new();
    private Random _random = new();
    #endregion

    #region Observable Properties
    public bool IsKioskMode { get; set; }

    [ObservableProperty] private MediaPlayer? _mediaPlayer;
    [ObservableProperty] private string? _playlistLogo;
    [ObservableProperty] private bool _isLoopEnabled;
    [ObservableProperty] private string _showPlayingText = "";
    [ObservableProperty] private bool _isShowPlayingVisible = false;
    [ObservableProperty] private double _showPlayingOpacity = 0.5;
    #endregion

    #region Fields & Constants
    private bool _isUserSeeking = false;
    private bool _isInternalUpdate = false;
    #endregion

    #region Observable Properties
    [ObservableProperty] private bool _isVlcReady;
    [ObservableProperty] private bool _isVisualizerVisible = true;
    #endregion

    #region Sub-ViewModels
    public JukeboxPlaylistViewModel PlaylistViewModel { get; } = new();
    public JukeboxEqViewModel EqViewModel { get; } = new();
    public JukeboxVisualizerViewModel VisualizerViewModel { get; } = new();
    #endregion

    public event EventHandler<short[]>? PcmDataAvailable;

    public JukeboxViewModel()
    {
        Volume = InitialVolume;
        EqViewModel.EqBandUpdated += (s, band) => UpdateBassEqBand(band);

        if (!IsVideoOnly)
        {
            Bass.Init(-1, 44100, DeviceInitFlags.Default, IntPtr.Zero);
            _bassTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _bassTimer.Tick += BassTimer_Tick;
            _bassTimer.Start();
        }

        if (!IsAudioOnly)
        {
            _ = InitializeVlcAsync();
        }

        _ = VisualizerViewModel.LoadVisualizersAsync();
    }

    private Task InitializeVlcAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                Core.Initialize();

                // Use the safe flags from Gamelist-Manager to prevent 10-second subtitle folder scans
                var options = new string[] 
                {
                    "--no-sub-autodetect-file",
                    "--no-video-title-show",
                    "--no-stats",
                    "--no-snapshot-preview",
                    "--no-media-library",
                    "--no-auto-preparse",
                    "--no-lua",
                    "--no-osd"
                };
                
                _libVLC = new LibVLC(options);
                var newPlayer = new MediaPlayer(_libVLC);
                newPlayer.TimeChanged += MediaPlayer_TimeChanged;
                newPlayer.PositionChanged += MediaPlayer_PositionChanged;
                newPlayer.EndReached += MediaPlayer_EndReached;
                newPlayer.Playing += MediaPlayer_Playing;
                newPlayer.Paused += MediaPlayer_Paused;
                newPlayer.Stopped += MediaPlayer_Stopped;

                Dispatcher.UIThread.Post(() => 
                {
                    MediaPlayer = newPlayer;
                    MediaPlayer.Volume = (int)Volume;
                    IsVlcReady = true;
                });
            }
            catch (Exception)
            {
                // LibVLC failed to load
            }
        });
    }

    private void MediaPlayer_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        if (_isUserSeeking) return;
        Dispatcher.UIThread.Post(() =>
        {
            _isInternalUpdate = true;
            var ts = TimeSpan.FromMilliseconds(e.Time);
            CurrentTimeString = $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
            PlaybackPosition = e.Time;
            _isInternalUpdate = false;
        });
    }

    private void MediaPlayer_PositionChanged(object? sender, MediaPlayerPositionChangedEventArgs e)
    {
        // We track Position in Ms, so TimeChanged handles it primarily.
    }

    private void MediaPlayer_EndReached(object? sender, EventArgs e)
    {
        // Media ends -> trigger Next safely via UIThread
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Next());
    }

    private void MediaPlayer_Playing(object? sender, EventArgs e)
    {
        if (_isAudioMode) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            CanPlay = false;
            CanPause = true;
            CanStop = true;

            // VLC frequently resets internal volume when spinning up a new audio thread.
            // Sync it safely after a tiny delay so the audio output is fully ready.
            await Task.Delay(50);
            if (MediaPlayer != null)
            {
                try { MediaPlayer.Volume = (int)Volume; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"VLC Vol Error: {ex.Message}"); }
            }
        });
    }

    private void MediaPlayer_Paused(object? sender, EventArgs e)
    {
        if (_isAudioMode) return;
        Dispatcher.UIThread.Post(() =>
        {
            CanPlay = true;
            CanPause = false;
            CanStop = true;
        });
    }

    private void MediaPlayer_Stopped(object? sender, EventArgs e)
    {
        if (_isAudioMode) return;
        Dispatcher.UIThread.Post(() =>
        {
            CanPlay = true;
            CanPause = false;
            CanStop = false;
            CurrentTimeString = "0:00";
            PlaybackPosition = 0;
        });
    }

    [ObservableProperty] private bool _isEqVisible = false;

    private int _bassStream = 0;
    private DSPProcedure? _bassDspProc;
    private bool _isAudioMode;
    private DispatcherTimer? _bassTimer;
    
    private static readonly string[] _audioExtensions = { ".mp3", ".flac", ".wav", ".ogg", ".m4a", ".wma" };

    private void UpdateBassEqBand(EqSliderViewModel band)
    {
        if (band.FxHandle != 0)
        {
            var p = new DXParamEQParameters
            {
                fBandwidth = 18f,
                fCenter = band.CenterFrequency,
                fGain = (float)band.Gain
            };
            Bass.FXSetParameters(band.FxHandle, p);
        }
    }

    private void BassTimer_Tick(object? sender, EventArgs e)
    {
        if (_bassStream != 0 && Bass.ChannelIsActive(_bassStream) == PlaybackState.Playing)
        {
            if (_isUserSeeking) return;
            long pos = Bass.ChannelGetPosition(_bassStream);
            double sec = Bass.ChannelBytes2Seconds(_bassStream, pos);
            var ts = TimeSpan.FromSeconds(sec);
            
            _isInternalUpdate = true;
            CurrentTimeString = $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
            PlaybackPosition = (long)(sec * 1000);
            _isInternalUpdate = false;
        }
        else if (_bassStream != 0 && Bass.ChannelIsActive(_bassStream) == PlaybackState.Stopped && CurrentTrack != null)
        {
            Next();
        }
    }

    private void OnBassDsp(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        if (length <= 0) return;
        short[] pcm = new short[length / 2];
        Marshal.Copy(buffer, pcm, 0, length / 2);
        PcmDataAvailable?.Invoke(this, pcm);
    }

    [ObservableProperty] private double _controlBarHeight = 65;
    
    [ObservableProperty] private bool _isPlaylistVisible;

    [ObservableProperty] private bool _isPickerVisible;

    partial void OnIsPlaylistVisibleChanged(bool value)
    {
        if (value) 
        {
            IsPickerVisible = false;
        }
    }

    partial void OnIsPickerVisibleChanged(bool value)
    {
        if (value) 
        {
            IsPlaylistVisible = false;
        }
    }

    [ObservableProperty] private string _currentTimeString = "0:00";
    [ObservableProperty] private string _totalTimeString = "0:00";
    
    private double _playbackPosition = 0;
    public double PlaybackPosition
    {
        get => _playbackPosition;
        set
        {
            if (SetProperty(ref _playbackPosition, value))
            {
                if (!_isInternalUpdate)
                {
                    if (_bassStream != 0 && Bass.ChannelIsActive(_bassStream) != PlaybackState.Stopped)
                    {
                        Bass.ChannelSetPosition(_bassStream, Bass.ChannelSeconds2Bytes(_bassStream, value / 1000.0));
                    }
                    else if (MediaPlayer != null)
                    {
                        MediaPlayer.Time = (long)value;
                    }
                }
            }
        }
    }
    
    [ObservableProperty] private double _playbackLength = 100;
    
    [ObservableProperty] private JukeboxTrack? _currentTrack = new JukeboxTrack { DisplayName = "GUI Design Mode - No Track Loaded" };

    [ObservableProperty] private bool _isRandomPlayback = false;
    [ObservableProperty] private bool _hasMultipleTracks = true;
    
    [ObservableProperty] private bool _canPlay = true;
    [ObservableProperty] private bool _canPause = true;
    [ObservableProperty] private bool _canStop = true;
    
    [ObservableProperty] private bool _isAutoHideEnabled = false;
    
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
                    try { MediaPlayer.Volume = (int)value; } catch { }
                }
            }
        }
    }

    private async Task TriggerShowPlayingOSDAsync()
    {
        if (_showPlayingCts != null)
        {
            _showPlayingCts.Cancel();
            _showPlayingCts.Dispose();
        }
        _showPlayingCts = new CancellationTokenSource();
        var token = _showPlayingCts.Token;

        ShowPlayingOpacity = 0.5;
        IsShowPlayingVisible = true;

        try
        {
            // Wait 3 seconds at 50% opacity
            await Task.Delay(3000, token); 
            
            // Manually fade out over 3 seconds (60 steps)
            int steps = 60;
            int delay = 3000 / steps;
            double stepDrop = 0.5 / steps;

            for (int i = 0; i < steps; i++)
            {
                await Task.Delay(delay, token);
                ShowPlayingOpacity -= stepDrop;
            }

            ShowPlayingOpacity = 0.0;
            IsShowPlayingVisible = false;
        }
        catch (TaskCanceledException)
        {
            // Aborted by another track play or toggle
        }
    }

    partial void OnIsShowPlayingEnabledChanged(bool value)
    {
        if (value && CurrentTrack != null)
        {
            ShowPlayingText = CurrentTrack.DisplayName;
            _ = TriggerShowPlayingOSDAsync();
        }
        else if (!value)
        {
            if (_showPlayingCts != null)
            {
                _showPlayingCts.Cancel();
                _showPlayingCts.Dispose();
                _showPlayingCts = null;
            }
            IsShowPlayingVisible = false;
        }
    }

    partial void OnCurrentTrackChanged(JukeboxTrack? value)
    {
        if (value == null)
        {
            MediaPlayer?.Stop();
            return;
        }

        if (IsShowPlayingEnabled)
        {
            ShowPlayingText = value.DisplayName;
            _ = TriggerShowPlayingOSDAsync();
        }

        if (!string.IsNullOrEmpty(value.FilePath))
        {
            // Extract UI length purely from the TagLib metadata we parsed earlier
            var parts = value.Length.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int m) && int.TryParse(parts[1], out int s))
            {
                PlaybackLength = (m * 60 + s) * 1000;
                TotalTimeString = value.Length;
            }
        }
    }

    [RelayCommand] 
    private void Previous() 
    {
        if (PlaylistViewModel.Playlist.Count == 0) return;
        var index = CurrentTrack != null ? PlaylistViewModel.Playlist.IndexOf(CurrentTrack) : -1;
        var oldTrack = CurrentTrack;

        if (index > 0)
            CurrentTrack = PlaylistViewModel.Playlist[index - 1];
        else if (IsLoopEnabled)
            CurrentTrack = PlaylistViewModel.Playlist[^1]; // loop to end
        else
            return; // don't loop
            
        PlayInternal(CurrentTrack == oldTrack);
    }
    
    [RelayCommand] 
    private void Pause() 
    { 
        if (_bassStream != 0 && Bass.ChannelIsActive(_bassStream) == PlaybackState.Playing)
        {
            Bass.ChannelPause(_bassStream);
            CanPlay = true;
            CanPause = false;
            CanStop = true;
        }
        else if (IsVlcReady && MediaPlayer != null && MediaPlayer.IsPlaying)
        {
            MediaPlayer.Pause();
        }
    }
    
    [RelayCommand] 
    private void Stop() 
    { 
        if (_bassStream != 0)
        {
            Bass.ChannelStop(_bassStream);
        }
        if (IsVlcReady && MediaPlayer != null)
        {
            MediaPlayer.Stop();
        }
        CurrentTrack = null;
        
        CanPlay = true;
        CanPause = false;
        CanStop = false;
        CurrentTimeString = "0:00";
        PlaybackPosition = 0;
    }
    
    [RelayCommand] 
    private void Play() => PlayInternal();

    private void PlayInternal(bool forceRestart = false)
    { 
        if (!IsVlcReady || MediaPlayer == null || _libVLC == null) return;

        if (CurrentTrack == null || string.IsNullOrEmpty(CurrentTrack.FilePath))
        {
            if (PlaylistViewModel.Playlist.Count > 0)
                CurrentTrack = PlaylistViewModel.Playlist[0];
            else
                return;
        }

        bool isAudio = _audioExtensions.Any(ext => CurrentTrack.FilePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        _isAudioMode = isAudio;
        if (!_isAudioMode && !ForceVisualizer)
        {
            IsVisualizerVisible = false;
        }
        else
        {
            IsVisualizerVisible = true;
        }

        var targetUri = new Uri(CurrentTrack.FilePath).AbsoluteUri;

        if (forceRestart || MediaPlayer.Media == null || MediaPlayer.Media.Mrl != targetUri)
        {
            var oldMedia = MediaPlayer.Media;
            var newMedia = new Media(_libVLC, CurrentTrack.FilePath, FromType.FromPath);
            
            MediaPlayer.Media = newMedia;

            if (isAudio)
            {
                Task.Run(() => { try { MediaPlayer?.Stop(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"VLC Stop Error: {ex.Message}"); } });
                if (_bassStream != 0) { Bass.StreamFree(_bassStream); _bassStream = 0; }
                
                _bassStream = Bass.CreateStream(CurrentTrack.FilePath, 0, 0, BassFlags.Default);
                if (_bassStream != 0)
                {
                    // Apply EQ effects
                    foreach (var band in EqViewModel.EqBands)
                    {
                        band.FxHandle = Bass.ChannelSetFX(_bassStream, EffectType.DXParamEQ, 0);
                        UpdateBassEqBand(band);
                    }

                    _bassDspProc = new DSPProcedure(OnBassDsp);
                    Bass.ChannelSetDSP(_bassStream, _bassDspProc);
                    Bass.ChannelSetAttribute(_bassStream, ChannelAttribute.Volume, Volume / 100.0);
                    Bass.ChannelPlay(_bassStream);
                    
                    CanPlay = false;
                    CanPause = true;
                    CanStop = true;
                    
                    long len = Bass.ChannelGetLength(_bassStream);
                    double sec = Bass.ChannelBytes2Seconds(_bassStream, len);
                    PlaybackLength = (long)(sec * 1000);
                    var ts = TimeSpan.FromSeconds(sec);
                    TotalTimeString = $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
                }
                return;
            }
            else
            {
                if (_bassStream != 0) { Bass.StreamFree(_bassStream); _bassStream = 0; }

                Task.Run(() => 
                {
                    try { MediaPlayer?.Stop(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"VLC Stop Error: {ex.Message}"); }
                    try { oldMedia?.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"VLC Dispose Error: {ex.Message}"); }
                    try { MediaPlayer?.Play(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"VLC Play Error: {ex.Message}"); }
                });
                return;
            }
        }

        if (isAudio)
        {
            if (_bassStream != 0 && Bass.ChannelIsActive(_bassStream) == PlaybackState.Paused)
            {
                Bass.ChannelPlay(_bassStream);
            }
            CanPlay = false;
            CanPause = true;
            CanStop = true;
        }
        else
        {
            if (MediaPlayer != null && !MediaPlayer.IsPlaying)
            {
                MediaPlayer.Play();
            }
        }
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
                if (IsLoopEnabled)
                    _playedIndices.Clear();
                else
                    return; // Done
            }

            int nextIndex;
            do
            {
                nextIndex = _random.Next(PlaylistViewModel.Playlist.Count);
            } while (_playedIndices.Contains(nextIndex) && _playedIndices.Count < PlaylistViewModel.Playlist.Count);

            _playedIndices.Add(nextIndex);
            CurrentTrack = PlaylistViewModel.Playlist[nextIndex];
        }
        else
        {
            var index = CurrentTrack != null ? PlaylistViewModel.Playlist.IndexOf(CurrentTrack) : -1;
            if (index >= 0 && index < PlaylistViewModel.Playlist.Count - 1)
            {
                CurrentTrack = PlaylistViewModel.Playlist[index + 1];
            }
            else
            {
                if (IsLoopEnabled)
                    CurrentTrack = PlaylistViewModel.Playlist[0]; // loop to start
                else
                    return; // Stop at end of list
            }
        }
            
        PlayInternal(CurrentTrack == oldTrack);
    }

    public async Task InitializeStartupAsync()
    {
        Volume = InitialVolume;

        if (!string.IsNullOrEmpty(InitialFile))
        {
            var files = new List<string> { InitialFile };
            await PlaylistViewModel.ProcessAndAddFilesAsync(files, NoRecurse);
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

    public void LoadSystemLogo(string systemName)
    {
        // Placeholder for restoring LoadSystemLogo
    }

    [RelayCommand] private void PlaySelectedTrack() { }
    [RelayCommand] private void ApplyPreset() { }
    [RelayCommand] private void ToggleMiniPlayer() { }
    [RelayCommand] private void ToggleVisualizer() { }

    [RelayCommand] private void TogglePlaylist() => IsPlaylistVisible = !IsPlaylistVisible;
    [RelayCommand] private void ToggleEq() => IsEqVisible = !IsEqVisible;
    [RelayCommand] private void TogglePicker() => IsPickerVisible = !IsPickerVisible;
    [RelayCommand] private void ToggleRandom() => IsRandomPlayback = !IsRandomPlayback;
    [RelayCommand] private void ToggleAutoHide() => IsAutoHideEnabled = !IsAutoHideEnabled;

    #region Public Methods
    public void Dispose()
    {
        if (_showPlayingCts != null)
        {
            _showPlayingCts.Cancel();
            _showPlayingCts.Dispose();
        }

        VisualizerViewModel?.Dispose();

        Bass.Free();
        var player = MediaPlayer;
        var media = MediaPlayer?.Media;
        var libVlc = _libVLC;

        MediaPlayer = null;

        Task.Run(() =>
        {
            try { player?.Stop(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"VLC Dispose Stop Error: {ex.Message}"); }
            try { media?.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"VLC Dispose Media Error: {ex.Message}"); }
            try { player?.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"VLC Dispose Player Error: {ex.Message}"); }
            try { libVlc?.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"VLC Dispose Lib Error: {ex.Message}"); }
        });
    }
    #endregion
}
