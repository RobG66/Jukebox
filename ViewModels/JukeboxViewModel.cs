using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;

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
    public string? PlaylistLogo { get; set; }
    public int InitialVolume { get; set; }
    public string? InitialFile { get; set; }
    public bool ForceVisualizer { get; set; }
    public bool IsLoopEnabled { get; set; }
    public bool IsKioskMode { get; set; }
    public bool StayOnTop { get; set; }

    private LibVLC? _libVLC;
    [ObservableProperty] private MediaPlayer? _mediaPlayer;
    private bool _isUserSeeking = false;
    private bool _isInternalUpdate = false;

    [ObservableProperty] private bool _isVlcReady;

    public JukeboxViewModel()
    {
        _ = InitializeVlcAsync();
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
        // Media ends -> trigger Next safely via Task.Run to allow LibVLC to clean up the event
        Task.Run(() =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Next());
        });
    }

    private void MediaPlayer_Playing(object? sender, EventArgs e)
    {
        // VLC frequently resets internal volume when spinning up a new audio thread.
        // Sync it to the UI immediately once it starts playing.
        if (MediaPlayer != null)
        {
            try { MediaPlayer.Volume = (int)Volume; } catch { }
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            CanPlay = false;
            CanPause = true;
            CanStop = true;
        });
    }

    private void MediaPlayer_Paused(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            CanPlay = true;
            CanPause = false;
            CanStop = true;
        });
    }

    private void MediaPlayer_Stopped(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            CanPlay = true;
            CanPause = false;
            CanStop = false;
            CurrentTimeString = "0:00";
            PlaybackPosition = 0;
        });
    }

    [ObservableProperty] private double _controlBarHeight = 65;
    
    [ObservableProperty] private bool _isPlaylistVisible;
    [ObservableProperty] private bool _isPickerVisible;

    partial void OnIsPlaylistVisibleChanged(bool value)
    {
        if (value) IsPickerVisible = false;
    }

    partial void OnIsPickerVisibleChanged(bool value)
    {
        if (value) IsPlaylistVisible = false;
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
                if (!_isInternalUpdate && MediaPlayer != null)
                {
                    MediaPlayer.Time = (long)value;
                }
            }
        }
    }
    
    [ObservableProperty] private double _playbackLength = 100;
    
    [ObservableProperty] private JukeboxTrack? _currentTrack = new JukeboxTrack { DisplayName = "GUI Design Mode - No Track Loaded" };
    
    public System.Collections.ObjectModel.ObservableCollection<JukeboxTrack> Playlist { get; } = new();

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
                if (MediaPlayer != null)
                {
                    try { MediaPlayer.Volume = (int)value; } catch { }
                }
            }
        }
    }

    partial void OnCurrentTrackChanged(JukeboxTrack? value)
    {
        if (value == null)
        {
            MediaPlayer?.Stop();
            return;
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
        if (Playlist.Count == 0) return;
        var index = CurrentTrack != null ? Playlist.IndexOf(CurrentTrack) : -1;
        if (index > 0)
            CurrentTrack = Playlist[index - 1];
        else
            CurrentTrack = Playlist[^1]; // loop to end
            
        Play();
    }
    
    [RelayCommand] 
    private void Pause() 
    { 
        if (!IsVlcReady || MediaPlayer == null) return;
        MediaPlayer.Pause();
    }
    
    [RelayCommand] 
    private void Stop() 
    { 
        if (!IsVlcReady || MediaPlayer == null) return;
        MediaPlayer.Stop();
        CurrentTrack = null;
    }
    
    [RelayCommand] 
    private void Play() 
    { 
        if (!IsVlcReady || MediaPlayer == null || _libVLC == null) return;

        if (CurrentTrack == null || string.IsNullOrEmpty(CurrentTrack.FilePath))
        {
            if (Playlist.Count > 0)
                CurrentTrack = Playlist[0];
            else
                return;
        }

        var targetUri = new Uri(CurrentTrack.FilePath).AbsoluteUri;

        if (MediaPlayer.Media == null || MediaPlayer.Media.Mrl != targetUri)
        {
            var oldMedia = MediaPlayer.Media;
            var newMedia = new Media(_libVLC, CurrentTrack.FilePath, FromType.FromPath);
            
            MediaPlayer.Media = newMedia;

            Task.Run(() => 
            {
                try { MediaPlayer?.Stop(); } catch { }
                try { oldMedia?.Dispose(); } catch { }
                try { MediaPlayer?.Play(); } catch { }
            });
            return;
        }

        if (!MediaPlayer.IsPlaying)
        {
            MediaPlayer.Play();
        }
    }
    
    [RelayCommand] 
    private void Next() 
    { 
        if (Playlist.Count == 0) return;
        var index = CurrentTrack != null ? Playlist.IndexOf(CurrentTrack) : -1;
        if (index >= 0 && index < Playlist.Count - 1)
            CurrentTrack = Playlist[index + 1];
        else
            CurrentTrack = Playlist[0]; // loop to start
            
        Play();
    }

    [RelayCommand]
    private void ClearPlaylist()
    {
        Playlist.Clear();
    }

    [RelayCommand]
    private void RemoveSelected(System.Collections.IList? selectedItems)
    {
        if (selectedItems == null) return;
        
        var itemsToRemove = selectedItems.Cast<JukeboxTrack>().ToList();
        foreach (var item in itemsToRemove)
        {
            Playlist.Remove(item);
        }
    }
    
    [RelayCommand] private void PlaySelectedTrack() { }
    [RelayCommand] private void ApplyPreset() { }
    [RelayCommand] private void ToggleMiniPlayer() { }
    [RelayCommand] private void ToggleVisualizer() { }

    public void Dispose()
    {
        var player = MediaPlayer;
        var media = MediaPlayer?.Media;
        var libVlc = _libVLC;

        MediaPlayer = null;

        Task.Run(() =>
        {
            try { player?.Stop(); } catch { }
            try { media?.Dispose(); } catch { }
            try { player?.Dispose(); } catch { }
            try { libVlc?.Dispose(); } catch { }
        });
    }
}
