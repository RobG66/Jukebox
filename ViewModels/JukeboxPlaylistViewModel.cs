using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jukebox.Extensions;
using Jukebox.Helpers;
using Jukebox.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Collections;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;

namespace Jukebox.ViewModels;

// Split across three partial files by concern:
//   JukeboxPlaylistViewModel.cs             - state, properties, filtering (this file)
//   JukeboxPlaylistViewModel.Persistence.cs - save/load/delete/clear/switch for saved playlists
//   JukeboxPlaylistViewModel.Import.cs      - adding tracks, file discovery, tag reading
public partial class JukeboxPlaylistViewModel : ViewModelBase
{
    #region Fields & Constants
    private bool _isSwitchingPlaylist = false;
    private int _playlistVersion = 0;
    private int _scrollVersion = 0;
    private int _pendingFirst = 0;
    private int _pendingLast = Constants.TagBatchSize - 1;
    #endregion

    #region Observable Properties
    [ObservableProperty] private string? _selectedLibraryPlaylist;
    [ObservableProperty] private string? _selectedRadioPlaylist;

    [ObservableProperty] private string _libraryPlaylistSummary = "0 Tracks | 0h 0m total";
    [ObservableProperty] private string _radioPlaylistSummary = "0 Stations | 0h 0m total";

    [ObservableProperty] private string _searchLibraryText = "";
    [ObservableProperty] private string _searchRadioText = "";

    [ObservableProperty] private bool _hasMultipleTracks = false;
    #endregion

    #region Public Properties
    public ObservableCollection<string> SavedLibraryPlaylists { get; } = new();
    public ObservableCollection<string> SavedRadioPlaylists { get; } = new();
    public ObservableCollection<JukeboxTrack> Playlist { get; } = new();
    public DataGridCollectionView FilteredLibraryPlaylist { get; }
    public DataGridCollectionView FilteredRadioPlaylist { get; }
    #endregion

    #region Constructor
    public JukeboxPlaylistViewModel()
    {
        FilteredLibraryPlaylist = new DataGridCollectionView(Playlist);
        FilteredLibraryPlaylist.Filter = FilterLibraryTrack;

        FilteredRadioPlaylist = new DataGridCollectionView(Playlist);
        FilteredRadioPlaylist.Filter = FilterRadioTrack;
    }
    #endregion

    #region Property Change Callbacks
    partial void OnSearchLibraryTextChanged(string value)
    {
        FilteredLibraryPlaylist.Refresh();
    }

    partial void OnSearchRadioTextChanged(string value)
    {
        FilteredRadioPlaylist.Refresh();
    }

    partial void OnSelectedLibraryPlaylistChanged(string? oldValue, string? newValue)
    {
        if (_isSwitchingPlaylist) return;
        SwitchLibraryPlaylistAsync(oldValue, newValue).SafeFireAndForget(nameof(SwitchLibraryPlaylistAsync));
    }

    partial void OnSelectedRadioPlaylistChanged(string? oldValue, string? newValue)
    {
        if (_isSwitchingPlaylist) return;
        SwitchRadioPlaylistAsync(oldValue, newValue).SafeFireAndForget(nameof(SwitchRadioPlaylistAsync));
    }
    #endregion

    #region Public Methods
    public event EventHandler? PlaylistCleared;

    // Raised when the currently-playing track is removed from either playlist.
    public event EventHandler? PlayingTrackRemoved;

    public void NotifyVisibleRange(int firstIndex, int lastIndex)
    {
        _pendingFirst = firstIndex;
        _pendingLast = lastIndex;
        int sv = ++_scrollVersion;
        TagVisibleRangeAsync(firstIndex, lastIndex, _playlistVersion, sv).SafeFireAndForget(nameof(TagVisibleRangeAsync));
    }

    // Removes the transient browser preview slot if one exists.
    // Called by the playback VM whenever the user plays a permanent playlist entry.
    public void RemoveTransientSlot()
    {
        var transient = Playlist.FirstOrDefault(t => t.IsTransient);
        if (transient != null)
        {
            Playlist.Remove(transient);
            HasMultipleTracks = Playlist.Count > 1;
            UpdatePlaylistSummary();
        }
    }
    #endregion

    #region Private Methods
    private bool FilterLibraryTrack(object arg)
    {
        if (arg is not JukeboxTrack track) return false;
        if (IsUrlTrack(track)) return false;

        if (string.IsNullOrWhiteSpace(SearchLibraryText)) return true;
        return track.DisplayName?.Contains(SearchLibraryText, StringComparison.OrdinalIgnoreCase) == true;
    }

    private bool FilterRadioTrack(object arg)
    {
        if (arg is not JukeboxTrack track) return false;
        if (!IsUrlTrack(track)) return false;

        if (string.IsNullOrWhiteSpace(SearchRadioText)) return true;
        return track.DisplayName?.Contains(SearchRadioText, StringComparison.OrdinalIgnoreCase) == true;
    }

    private void InvalidatePlaylist()
    {
        _playlistVersion++;
        _scrollVersion++;
    }

    private void UpdatePlaylistSummary()
    {
        var libTracks = Playlist.Where(t => !IsUrlTrack(t)).ToList();
        double libSeconds = libTracks.Sum(t => t.Length.TotalSeconds);
        if (libSeconds > 0)
        {
            int libHours = (int)libSeconds / 3600;
            int libMinutes = ((int)libSeconds % 3600) / 60;
            LibraryPlaylistSummary = $"{libTracks.Count} Track{(libTracks.Count != 1 ? "s" : "")} | {libHours}h {libMinutes}m total";
        }
        else
        {
            LibraryPlaylistSummary = $"{libTracks.Count} Track{(libTracks.Count != 1 ? "s" : "")}";
        }

        // Transient preview slots are excluded from the station count summary.
        var radTracks = Playlist.Where(t => IsUrlTrack(t) && !t.IsTransient).ToList();
        double radSeconds = radTracks.Sum(t => t.Length.TotalSeconds);
        if (radSeconds > 0)
        {
            int radHours = (int)radSeconds / 3600;
            int radMinutes = ((int)radSeconds % 3600) / 60;
            RadioPlaylistSummary = $"{radTracks.Count} Station{(radTracks.Count != 1 ? "s" : "")} | {radHours}h {radMinutes}m total";
        }
        else
        {
            RadioPlaylistSummary = $"{radTracks.Count} Station{(radTracks.Count != 1 ? "s" : "")}";
        }
    }
    #endregion

    #region Helpers
    public static bool IsUrlTrack(JukeboxTrack track)
    {
        return track.FilePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               track.FilePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }
    #endregion
}
