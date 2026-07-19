using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jukebox.Extensions;
using Jukebox.Models;
using Jukebox.Plugin.Abstractions;
using Jukebox.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Collections;
using System.Linq;
using System.Threading.Tasks;

namespace Jukebox.ViewModels;

public enum PlaylistTabType
{
    PlayQueue,
    SavedPlaylists,
    Plugins
}

public enum PlaylistTarget
{
    PlayQueue,
    SelectedSavedPlaylist
}

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

    private readonly IUserDialogService _dialogService;
    #endregion

    #region Observable Properties
    [ObservableProperty] private string? _selectedLibraryPlaylist;

    [ObservableProperty] private string _libraryPlaylistSummary = "0 Tracks";

    [ObservableProperty] private string _playQueueSummary = "0 Tracks";

    [ObservableProperty] private string _searchLibraryText = "";

    [ObservableProperty] private bool _hasMultipleTracks = false;

    [ObservableProperty] private int _activeTabIndex;

    [ObservableProperty] private int _lastHostTabIndex;
    #endregion

    #region Public Properties
    // Transient runtime playback order. This collection is never replaced so
    // playback subscriptions and UI bindings remain stable.
    public ObservableCollection<JukeboxTrack> PlayQueue { get; } = new();

    // The queue grid uses a view so explicit reorder operations can refresh
    // the visible order reliably across DataGrid collection-change handling.
    public DataGridCollectionView PlayQueueView { get; }

    // The currently selected persisted library playlist.
    public ObservableCollection<JukeboxTrack> LibraryPlaylist { get; } = new();

    // Search-only filtered views.
    public DataGridCollectionView FilteredLibraryPlaylist { get; }

    public ObservableCollection<string> SavedLibraryPlaylists { get; } = new();

    public PlaylistTabType ActiveTab => ActiveTabIndex switch
    {
        0 => PlaylistTabType.PlayQueue,
        1 => PlaylistTabType.SavedPlaylists,
        _ => PlaylistTabType.Plugins
    };
    #endregion

    #region Plugin Media Browsers
    /// <summary>
    /// Plugin media browser tabs discovered at startup by
    /// <see cref="Jukebox.Services.PluginLoader"/>. Each entry is a
    /// <see cref="PluginTab"/> wrapper holding the browser interface and
    /// its created <c>UserControl</c>. Populated by App.axaml.cs after
    /// plugins are loaded, before the main window is shown.
    ///
    /// The View (PlaylistView.axaml.cs) iterates this collection in its
    /// Loaded handler and creates a <c>TabItem</c> for each entry. This
    /// is the only place the main app touches plugin types — after this
    /// collection is populated, adding or removing a plugin requires NO
    /// changes to the main app source code.
    /// </summary>
    public ObservableCollection<PluginTab> MediaBrowserTabs { get; } = new();

    /// <summary>
    /// Releases every host-owned browser exactly once during application shutdown.
    /// Browser instances remain responsible for their private view-model state.
    /// </summary>
    public void DisposeMediaBrowsers()
    {
        foreach (var pluginTab in MediaBrowserTabs)
        {
            try
            {
                pluginTab.Browser.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Plugin] Dispose failed for {pluginTab.Browser.Id}: {ex.Message}");
            }
        }
    }
    #endregion



    #region Constructor
    public JukeboxPlaylistViewModel() : this(null) { }

    // Constructor for testability — tests can inject a stub dialog service.
    public JukeboxPlaylistViewModel(IUserDialogService? dialogService)
    {
        _dialogService = dialogService ?? new UserDialogService();
        PlayQueueView = new DataGridCollectionView(PlayQueue);
        FilteredLibraryPlaylist = new DataGridCollectionView(LibraryPlaylist);
        FilteredLibraryPlaylist.Filter = FilterLibraryTrack;
    }
    #endregion

    #region Property Change Callbacks
    partial void OnSearchLibraryTextChanged(string value)
    {
        FilteredLibraryPlaylist.Refresh();
    }

    partial void OnSelectedLibraryPlaylistChanged(string? oldValue, string? newValue)
    {
        if (_isSwitchingPlaylist) return;
        SwitchLibraryPlaylistAsync(oldValue, newValue).SafeFireAndForget(nameof(SwitchLibraryPlaylistAsync));
    }

    partial void OnActiveTabIndexChanged(int value)
    {
        if (value is 0 or 1)
        {
            LastHostTabIndex = value;
        }

        OnPropertyChanged(nameof(ActiveTab));
    }
    #endregion

    #region Public Methods
    /// <summary>
    /// Raised after the runtime queue has been explicitly replaced. Collection
    /// change notifications alone are not sufficient here because appending a
    /// track must not reset shuffle history, while replacement must.
    /// </summary>
    public event EventHandler? PlayQueueReplaced;

    /// <summary>Raised after the runtime queue has been explicitly cleared.</summary>
    public event EventHandler? PlayQueueCleared;

    /// <summary>
    /// Raised when a queue removal operation removes the currently-playing
    /// queue item. Saved-playlist edits never raise this event.
    /// </summary>
    public event EventHandler? PlayingQueueTrackRemoved;

    /// <summary>
    /// Replaces the contents of the runtime play queue without replacing the
    /// observable collection instance.
    /// </summary>
    public void ReplacePlayQueue(IEnumerable<JukeboxTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        // Materialize first in case the source is PlayQueue itself or another
        // collection that changes while the queue is being rebuilt.
        var replacement = tracks.ToList();

        foreach (var existingTrack in PlayQueue)
        {
            existingTrack.IsPlaying = false;
        }

        PlayQueue.Clear();
        foreach (var track in replacement)
        {
            track.IsPlaying = false;
            PlayQueue.Add(track);
        }

        UpdatePlaylistSummary();
        PlayQueueReplaced?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Adds tracks to the end of the runtime play queue, ignoring tracks that
    /// are already queued.
    /// </summary>
    public void AppendToPlayQueue(IEnumerable<JukeboxTrack> tracks)
    {
        AppendNewToPlayQueue(tracks);
    }

    private IReadOnlyList<JukeboxTrack> AppendNewToPlayQueue(IEnumerable<JukeboxTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        var additions = GetNewPlayQueueTracks(tracks);
        foreach (var track in additions)
        {
            track.IsPlaying = false;
            PlayQueue.Add(track);
        }

        UpdatePlaylistSummary();
        return additions;
    }

    /// <summary>
    /// Inserts tracks immediately after the current queue item. If there is
    /// no current queue item, the tracks are inserted at the beginning.
    /// Insertion is not a queue replacement, so valid shuffle history remains.
    /// </summary>
    public void InsertNextInPlayQueue(
        IEnumerable<JukeboxTrack> tracks,
        JukeboxTrack? currentTrack)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        var additions = GetNewPlayQueueTracks(tracks);
        int currentIndex = currentTrack is null ? -1 : PlayQueue.IndexOf(currentTrack);
        int insertionIndex = currentIndex >= 0 ? currentIndex + 1 : 0;

        foreach (var track in additions)
        {
            track.IsPlaying = false;
            PlayQueue.Insert(insertionIndex++, track);
        }

        UpdatePlaylistSummary();
    }

    internal JukeboxTrack? FindPlayQueueTrack(JukeboxTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (PlayQueue.Contains(track))
        {
            return track;
        }

        string? identity = GetQueueTrackIdentity(track);
        return identity == null
            ? null
            : PlayQueue.FirstOrDefault(existingTrack =>
                string.Equals(
                    GetQueueTrackIdentity(existingTrack),
                    identity,
                    StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Clears the runtime queue while preserving its binding instance.</summary>
    [RelayCommand]
    public void ClearPlayQueue()
    {
        foreach (var track in PlayQueue)
        {
            track.IsPlaying = false;
        }

        PlayQueue.Clear();
        UpdatePlaylistSummary();
        PlayQueueCleared?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void RemovePlayQueueSelected(System.Collections.IList? selectedItems)
    {
        if (selectedItems == null) return;
        RemoveFromPlayQueue(selectedItems.Cast<JukeboxTrack>().ToList());
    }

    /// <summary>
    /// Removes the supplied queue items. If the playing row is among them, the
    /// playback coordinator is notified after the collection mutation.
    /// </summary>
    public void RemoveFromPlayQueue(IEnumerable<JukeboxTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        var removals = tracks.Distinct().ToList();
        bool removedPlayingTrack = false;

        foreach (var track in removals)
        {
            if (!PlayQueue.Contains(track))
            {
                continue;
            }

            removedPlayingTrack |= track.IsPlaying;
            track.IsPlaying = false;
            PlayQueue.Remove(track);
        }

        UpdatePlaylistSummary();

        if (removedPlayingTrack)
        {
            PlayingQueueTrackRemoved?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Updates a queue item's transient playable URL while preserving the
    /// stable source URL that identifies it.
    /// </summary>
    public void UpdatePlayQueueUrl(string originalUrl, string resolvedUrl)
    {
        if (string.IsNullOrWhiteSpace(originalUrl) || string.IsNullOrWhiteSpace(resolvedUrl))
        {
            return;
        }

        foreach (var track in PlayQueue)
        {
            bool matchesStableSource = string.Equals(
                track.OriginalUrl,
                originalUrl,
                StringComparison.OrdinalIgnoreCase);
            bool matchesCurrentSource = string.Equals(
                track.FilePath,
                originalUrl,
                StringComparison.OrdinalIgnoreCase);

            if (!matchesStableSource && !matchesCurrentSource)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(track.OriginalUrl))
            {
                track.OriginalUrl = originalUrl;
            }

            track.FilePath = resolvedUrl;
        }
    }

    /// <summary>Creates an independent copy suitable for another collection.</summary>
    public static JukeboxTrack CopyTrack(JukeboxTrack source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new JukeboxTrack
        {
            DisplayName = source.DisplayName,
            FilePath = source.FilePath,
            OriginalUrl = source.OriginalUrl,
            SourcePluginId = source.SourcePluginId,
            Length = source.Length,
            Bitrate = source.Bitrate,
            Genre = source.Genre,
            Country = source.Country,
            Location = source.Location,
            IsTagged = source.IsTagged
        };
    }

    [RelayCommand]
    private void MovePlayQueueSelectedUp(System.Collections.IList? selectedItems)
    {
        if (MoveTracks(PlayQueue, GetSelectedTracks(selectedItems), -1))
        {
            PlayQueueView.Refresh();
            UpdatePlaylistSummary();
        }
    }

    [RelayCommand]
    private void MovePlayQueueSelectedDown(System.Collections.IList? selectedItems)
    {
        if (MoveTracks(PlayQueue, GetSelectedTracks(selectedItems), 1))
        {
            PlayQueueView.Refresh();
            UpdatePlaylistSummary();
        }
    }

    private static List<JukeboxTrack> GetSelectedTracks(
        System.Collections.IList? selectedItems)
    {
        return selectedItems?
            .Cast<object>()
            .OfType<JukeboxTrack>()
            .Distinct()
            .ToList()
            ?? new List<JukeboxTrack>();
    }

    internal static bool MoveTracks(
        ObservableCollection<JukeboxTrack> tracks,
        IReadOnlyCollection<JukeboxTrack> selectedTracks,
        int offset)
    {
        if (selectedTracks.Count == 0 || offset is not (-1 or 1))
        {
            return false;
        }

        var selected = selectedTracks.ToHashSet();
        bool moved = false;
        var orderedTracks = offset < 0
            ? tracks.Where(selected.Contains).ToList()
            : tracks.Where(selected.Contains).Reverse().ToList();

        foreach (var track in orderedTracks)
        {
            int oldIndex = tracks.IndexOf(track);
            int newIndex = oldIndex + offset;
            if (oldIndex < 0 || newIndex < 0 || newIndex >= tracks.Count)
            {
                continue;
            }

            // Treat the selection as a group. This preserves the relative
            // order of adjacent selections while also supporting gaps.
            if (selected.Contains(tracks[newIndex]))
            {
                continue;
            }

            tracks.Move(oldIndex, newIndex);
            moved = true;
        }

        return moved;
    }

    public void NotifyVisibleRange(int firstIndex, int lastIndex)
    {
        _pendingFirst = firstIndex;
        _pendingLast = lastIndex;
        int sv = ++_scrollVersion;
        TagVisibleRangeAsync(firstIndex, lastIndex, _playlistVersion, sv).SafeFireAndForget(nameof(TagVisibleRangeAsync));
    }

    #endregion

    #region Private Methods
    private List<JukeboxTrack> GetNewPlayQueueTracks(IEnumerable<JukeboxTrack> tracks)
    {
        var queuedIdentities = PlayQueue
            .Select(GetQueueTrackIdentity)
            .Where(identity => identity != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var additions = new List<JukeboxTrack>();
        foreach (var track in tracks)
        {
            string? identity = GetQueueTrackIdentity(track);
            if (identity != null && !queuedIdentities.Add(identity))
            {
                continue;
            }

            additions.Add(track);
        }

        return additions;
    }

    private static string? GetQueueTrackIdentity(JukeboxTrack track)
    {
        if (!string.IsNullOrWhiteSpace(track.OriginalUrl))
        {
            return track.OriginalUrl.Trim();
        }

        return string.IsNullOrWhiteSpace(track.FilePath)
            ? null
            : track.FilePath.Trim();
    }

    private bool FilterLibraryTrack(object arg)
    {
        if (arg is not JukeboxTrack track) return false;
        if (string.IsNullOrWhiteSpace(SearchLibraryText)) return true;
        return track.DisplayName?.Contains(SearchLibraryText, StringComparison.OrdinalIgnoreCase) == true;
    }

    private void InvalidatePlaylist()
    {
        _playlistVersion++;
        _scrollVersion++;
    }

    public void UpdatePlaylistSummary()
    {
        HasMultipleTracks = PlayQueue.Count > 1;

        var queueTracks = PlayQueue.ToList();
        PlayQueueSummary = BuildSummary(queueTracks);

        var libTracks = LibraryPlaylist.ToList();
        LibraryPlaylistSummary = BuildSummary(libTracks);
    }

    private static string BuildSummary(IReadOnlyCollection<JukeboxTrack> tracks)
    {
        string countText = $"{tracks.Count} Track{(tracks.Count == 1 ? string.Empty : "s")}";
        double totalSeconds = tracks.Sum(t => t.Length.TotalSeconds);
        if (totalSeconds <= 0)
        {
            return countText;
        }

        var total = TimeSpan.FromSeconds(totalSeconds);
        return total.TotalHours >= 1
            ? $"{countText}  •  {(int)total.TotalHours}h {total.Minutes}m"
            : $"{countText}  •  {total.Minutes}m {total.Seconds}s";
    }
    #endregion
}

/// <summary>
/// One plugin destination — the browser interface plus its created
/// <c>UserControl</c>. Populated by <c>App.axaml.cs</c> after
/// <see cref="PluginLoader"/> finishes, then iterated by
/// <c>PlaylistView.axaml.cs</c> to build <c>TabItem</c>s.
/// </summary>
public sealed class PluginTab
{
    public IJukeboxMediaBrowser Browser { get; init; } = null!;
    public Avalonia.Controls.UserControl View { get; init; } = null!;
}
