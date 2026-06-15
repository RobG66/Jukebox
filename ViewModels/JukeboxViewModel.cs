using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Jukebox.ViewModels;

public class JukeboxTrack
{
    public string DisplayName { get; set; } = "Unknown Track";
    public string Length { get; set; } = "0:00";
    public string Bitrate { get; set; } = "128 kbps";
    public string FilePath { get; set; } = string.Empty;
    public bool IsSelected { get; set; }
}

public partial class JukeboxViewModel : ViewModelBase
{
    public string? PlaylistLogo { get; set; }
    public int InitialVolume { get; set; }
    public string? InitialFile { get; set; }
    public bool ForceVisualizer { get; set; }
    public bool IsLoopEnabled { get; set; }
    public bool IsKioskMode { get; set; }
    public bool StayOnTop { get; set; }

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
    [ObservableProperty] private string _totalTimeString = "3:45";
    [ObservableProperty] private double _playbackPosition = 0;
    [ObservableProperty] private double _playbackLength = 100;
    
    [ObservableProperty] private JukeboxTrack? _currentTrack = new JukeboxTrack { DisplayName = "GUI Design Mode - No Track Loaded" };
    
    public System.Collections.ObjectModel.ObservableCollection<JukeboxTrack> Playlist { get; } = new();

    [ObservableProperty] private bool _isRandomPlayback = false;
    [ObservableProperty] private bool _hasMultipleTracks = true;
    
    [ObservableProperty] private bool _canPlay = true;
    [ObservableProperty] private bool _canPause = true;
    [ObservableProperty] private bool _canStop = true;
    
    [ObservableProperty] private bool _isAutoHideEnabled = false;
    [ObservableProperty] private double _volume = 75;

    [RelayCommand] 
    private void Previous() 
    {
        if (Playlist.Count == 0) return;
        var index = CurrentTrack != null ? Playlist.IndexOf(CurrentTrack) : -1;
        if (index > 0)
            CurrentTrack = Playlist[index - 1];
        else
            CurrentTrack = Playlist[^1]; // loop to end
    }
    
    [RelayCommand] 
    private void Pause() 
    { 
        // Pause logic here
    }
    
    [RelayCommand] 
    private void Stop() 
    { 
        CurrentTrack = null;
    }
    
    [RelayCommand] 
    private void Play() 
    { 
        if (CurrentTrack == null && Playlist.Count > 0)
        {
            CurrentTrack = Playlist[0];
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
    }

    [RelayCommand]
    private void AddFiles()
    {
        Playlist.Add(new JukeboxTrack { DisplayName = "New Added Track", Length = "3:30", Bitrate = "320 kbps" });
    }

    [RelayCommand]
    private void AddFolder()
    {
        Playlist.Add(new JukeboxTrack { DisplayName = "Added from Folder", Length = "2:15", Bitrate = "256 kbps" });
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

    [RelayCommand]
    private void ClearPlaylist()
    {
        Playlist.Clear();
    }
    
    [RelayCommand] private void PlaySelectedTrack() { }
    [RelayCommand] private void ApplyPreset() { }
    [RelayCommand] private void ToggleMiniPlayer() { }
    [RelayCommand] private void ToggleVisualizer() { }
}
