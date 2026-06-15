using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Linq;

namespace Jukebox.ViewModels;

public partial class JukeboxPlaylistViewModel : ViewModelBase
{
    public ObservableCollection<JukeboxTrack> Playlist { get; } = new();

    [ObservableProperty] private bool _hasMultipleTracks = false;

    public JukeboxPlaylistViewModel()
    {
        Playlist.CollectionChanged += (s, e) => 
        {
            HasMultipleTracks = Playlist.Count > 1;
        };
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
}
