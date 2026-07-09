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

public partial class JukeboxPlaylistViewModel : ViewModelBase
{
    #region Fields & Constants
    private string PlaylistsDirectory => Path.Combine(Jukebox.Services.PathProvider.Current.SettingsDirectory, "Playlists");
    private string ActiveLibraryPlaylistStateFile => Path.Combine(Jukebox.Services.PathProvider.Current.SettingsDirectory, "ActiveLibraryPlaylist.txt");
    private string ActiveRadioPlaylistStateFile => Path.Combine(Jukebox.Services.PathProvider.Current.SettingsDirectory, "ActiveRadioPlaylist.txt");

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

    public async Task ProcessAndAddFilesAsync(List<string> paths, bool noRecurse = false)
    {
        InvalidatePlaylist();
        int version = _playlistVersion;

        var filePaths = await Task.Run(() => DiscoverFiles(paths, noRecurse));

        if (_playlistVersion != version) return;

        foreach (var path in filePaths)
        {
            string displayName;
            if (path.Contains('|'))
            {
                var parts = path.Split('|', 2);
                string entryName = parts[1];
                string zipName = Path.GetFileNameWithoutExtension(parts[0]);
                displayName = $"{Path.GetFileNameWithoutExtension(entryName)} [{zipName}]";
            }
            else
            {
                displayName = Path.GetFileNameWithoutExtension(path);
            }

            Playlist.Add(new JukeboxTrack
            {
                DisplayName = displayName,
                FilePath = path
            });
        }

        HasMultipleTracks = Playlist.Count > 1;
        UpdatePlaylistSummary();

        int sv = ++_scrollVersion;
        TagVisibleRangeAsync(_pendingFirst, _pendingLast, version, sv).SafeFireAndForget(nameof(TagVisibleRangeAsync));

        await AutoSaveCurrentPlaylistAsync();
    }

    public async Task AddUrlTrackAsync(string url)
    {
        var existing = Playlist.FirstOrDefault(t => t.FilePath.Equals(url, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return;

        InvalidatePlaylist();

        string streamType = "—";
        if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            streamType = "HLS";
        else if (url.Contains(".m3u", StringComparison.OrdinalIgnoreCase))
            streamType = "M3U";
        else if (url.Contains(".pls", StringComparison.OrdinalIgnoreCase))
            streamType = "PLS";
        else if (url.Contains(".ashx", StringComparison.OrdinalIgnoreCase))
            streamType = "ASHX";
        else if (url.Contains(".mp3", StringComparison.OrdinalIgnoreCase))
            streamType = "MP3";
        else if (url.Contains(".flac", StringComparison.OrdinalIgnoreCase))
            streamType = "FLAC";

        var track = new JukeboxTrack
        {
            DisplayName = "Loading Stream Title...",
            FilePath = url,
            Bitrate = streamType,
            Genre = "—",
            Country = "—",
            IsTagged = true
        };

        Playlist.Add(track);
        HasMultipleTracks = Playlist.Count > 1;
        UpdatePlaylistSummary();

        FetchUrlMetadataAsync(track).SafeFireAndForget(nameof(FetchUrlMetadataAsync));

        await AutoSaveCurrentPlaylistAsync();

        await Task.CompletedTask;
    }

    public async Task<JukeboxTrack> AddRadioStationTrackAsync(string name, string url, string? codec = null, int? bitrate = null, string? genre = null, string? country = null)
    {
        var existing = Playlist.FirstOrDefault(t => t.FilePath.Equals(url, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            // If the existing entry is still a transient browser preview slot, promote
            // it to a permanent entry rather than silently returning it unchanged. This
            // covers the case where "Add to Playlist" is clicked in the radio browser
            // while the same station is already playing as the transient preview slot.
            if (existing.IsTransient)
            {
                existing.IsTransient = false;
                HasMultipleTracks = Playlist.Count > 1;
                UpdatePlaylistSummary();
                await AutoSaveCurrentPlaylistAsync();
            }
            return existing;
        }

        InvalidatePlaylist();

        string bitrateString = "—";
        if (!string.IsNullOrEmpty(codec))
        {
            bitrateString = (bitrate.HasValue && bitrate.Value > 0)
                ? $"{codec} ({bitrate.Value} kbps)"
                : codec;
        }
        else if (bitrate.HasValue && bitrate.Value > 0)
        {
            bitrateString = $"{bitrate.Value} kbps";
        }
        else if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            bitrateString = "HLS";
        }
        else if (url.Contains(".m3u", StringComparison.OrdinalIgnoreCase))
        {
            bitrateString = "M3U";
        }

        var track = new JukeboxTrack
        {
            DisplayName = name,
            FilePath = url,
            Bitrate = bitrateString,
            Genre = string.IsNullOrWhiteSpace(genre) ? "—" : genre,
            Country = ResolveCountryName(country),
            IsTagged = true
        };

        Playlist.Add(track);
        HasMultipleTracks = Playlist.Count > 1;
        UpdatePlaylistSummary();

        await AutoSaveCurrentPlaylistAsync();

        return track;
    }

    // Inserts (or replaces) the single transient "Now Playing" browser preview
    // slot at the head of the radio section. The slot is visually distinct in
    // the Radio playlist tab but is never persisted to disk and is replaced the
    // next time the user plays a station from the radio browser.
    public async Task<JukeboxTrack> SetTransientRadioStationAsync(
        string name, string url, string? codec = null, int? bitrate = null, string? genre = null, string? country = null)
    {
        // Remove any existing transient slot first.
        var existingTransient = Playlist.FirstOrDefault(t => t.IsTransient);
        if (existingTransient != null)
        {
            Playlist.Remove(existingTransient);
        }

        string bitrateString = "—";
        if (!string.IsNullOrEmpty(codec))
        {
            bitrateString = (bitrate.HasValue && bitrate.Value > 0)
                ? $"{codec} ({bitrate.Value} kbps)"
                : codec;
        }
        else if (bitrate.HasValue && bitrate.Value > 0)
        {
            bitrateString = $"{bitrate.Value} kbps";
        }
        else if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            bitrateString = "HLS";
        }
        else if (url.Contains(".m3u", StringComparison.OrdinalIgnoreCase))
        {
            bitrateString = "M3U";
        }

        var track = new JukeboxTrack
        {
            DisplayName = name,
            FilePath = url,
            Bitrate = bitrateString,
            Genre = string.IsNullOrWhiteSpace(genre) ? "—" : genre,
            Country = ResolveCountryName(country),
            IsTagged = true,
            IsTransient = true
        };

        // Insert the transient slot at the beginning of the radio section
        // (i.e. before the first existing URL track, or at the end if none).
        int insertIndex = Playlist.Count;
        for (int i = 0; i < Playlist.Count; i++)
        {
            if (IsUrlTrack(Playlist[i]))
            {
                insertIndex = i;
                break;
            }
        }
        Playlist.Insert(insertIndex, track);

        HasMultipleTracks = Playlist.Count > 1;
        UpdatePlaylistSummary();

        // Transient tracks are intentionally NOT auto-saved to disk.
        return track;
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

    public async Task InitializeAsync()
    {
        try
        {
            var libraryFolder = Path.Combine(PlaylistsDirectory, "Library");
            var radioFolder = Path.Combine(PlaylistsDirectory, "Radio");

            if (!Directory.Exists(libraryFolder)) Directory.CreateDirectory(libraryFolder);
            if (!Directory.Exists(radioFolder)) Directory.CreateDirectory(radioFolder);

            var libraryFiles = Directory.GetFiles(libraryFolder, "*.json");
            SavedLibraryPlaylists.Clear();
            foreach (var file in libraryFiles)
            {
                SavedLibraryPlaylists.Add(Path.GetFileNameWithoutExtension(file));
            }
            if (SavedLibraryPlaylists.Count == 0)
            {
                SavedLibraryPlaylists.Add("Default");
                await SavePlaylistToFileInternalAsync("Default", isRadio: false);
            }

            var radioFiles = Directory.GetFiles(radioFolder, "*.json");
            SavedRadioPlaylists.Clear();
            foreach (var file in radioFiles)
            {
                SavedRadioPlaylists.Add(Path.GetFileNameWithoutExtension(file));
            }
            if (SavedRadioPlaylists.Count == 0)
            {
                SavedRadioPlaylists.Add("Default");
                await SavePlaylistToFileInternalAsync("Default", isRadio: true);
            }

            var activeLibraryName = await LoadActivePlaylistNameAsync(isRadio: false);
            if (!SavedLibraryPlaylists.Contains(activeLibraryName)) activeLibraryName = SavedLibraryPlaylists[0];

            var activeRadioName = await LoadActivePlaylistNameAsync(isRadio: true);
            if (!SavedRadioPlaylists.Contains(activeRadioName)) activeRadioName = SavedRadioPlaylists[0];

            _isSwitchingPlaylist = true;
            SelectedLibraryPlaylist = activeLibraryName;
            SelectedRadioPlaylist = activeRadioName;
            _isSwitchingPlaylist = false;

            Playlist.Clear();

            var libraryTracks = await LoadPlaylistFromFileInternalAsync(activeLibraryName, isRadio: false);
            foreach (var track in libraryTracks) Playlist.Add(track);

            var radioTracks = await LoadPlaylistFromFileInternalAsync(activeRadioName, isRadio: true);
            foreach (var track in radioTracks) Playlist.Add(track);

            HasMultipleTracks = Playlist.Count > 1;
            UpdatePlaylistSummary();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Playlist] InitializeAsync failed: {ex.Message}");
        }
    }

    public async Task AutoSaveCurrentPlaylistAsync()
    {
        if (!string.IsNullOrEmpty(SelectedLibraryPlaylist))
        {
            await SavePlaylistToFileInternalAsync(SelectedLibraryPlaylist, isRadio: false);
        }
        if (!string.IsNullOrEmpty(SelectedRadioPlaylist))
        {
            await SavePlaylistToFileInternalAsync(SelectedRadioPlaylist, isRadio: true);
        }
    }
    #endregion

    #region Commands
    // Promotes the transient browser preview slot to a permanent playlist entry.
    // Clears the IsTransient flag in-place so the row stays at its current
    // position in the list, then auto-saves the radio playlist to disk so the
    // station is persisted for the next session.
    [RelayCommand]
    private async Task PromoteTransientToPlaylistAsync()
    {
        var transient = Playlist.FirstOrDefault(t => t.IsTransient);
        if (transient == null) return;

        transient.IsTransient = false;
        HasMultipleTracks = Playlist.Count > 1;
        UpdatePlaylistSummary();

        await AutoSaveCurrentPlaylistAsync();
    }

    [RelayCommand]
    private async Task SaveLibraryPlaylistAsAsync()
    {
        var name = await Jukebox.Views.TextInputDialogView.ShowAsync(
            "Save Library Playlist As",
            "Enter playlist name:",
            validator: val =>
            {
                if (string.IsNullOrWhiteSpace(val)) return (false, "Name cannot be empty.");
                var invalid = Path.GetInvalidFileNameChars();
                if (val.Any(c => invalid.Contains(c))) return (false, "Name contains invalid characters.");
                return (true, string.Empty);
            },
            okButtonText: "Save"
        );

        if (string.IsNullOrWhiteSpace(name)) return;

        await SavePlaylistToFileInternalAsync(name, isRadio: false);

        if (!SavedLibraryPlaylists.Contains(name))
        {
            SavedLibraryPlaylists.Add(name);
        }

        SelectedLibraryPlaylist = name;
        await SaveActivePlaylistNameAsync(name, isRadio: false);
    }

    [RelayCommand]
    private async Task SaveRadioPlaylistAsAsync()
    {
        var name = await Jukebox.Views.TextInputDialogView.ShowAsync(
            "Save Radio Playlist As",
            "Enter playlist name:",
            validator: val =>
            {
                if (string.IsNullOrWhiteSpace(val)) return (false, "Name cannot be empty.");
                var invalid = Path.GetInvalidFileNameChars();
                if (val.Any(c => invalid.Contains(c))) return (false, "Name contains invalid characters.");
                return (true, string.Empty);
            },
            okButtonText: "Save"
        );

        if (string.IsNullOrWhiteSpace(name)) return;

        await SavePlaylistToFileInternalAsync(name, isRadio: true);

        if (!SavedRadioPlaylists.Contains(name))
        {
            SavedRadioPlaylists.Add(name);
        }

        SelectedRadioPlaylist = name;
        await SaveActivePlaylistNameAsync(name, isRadio: true);
    }

    [RelayCommand]
    private async Task DeleteLibraryPlaylistAsync()
    {
        if (string.IsNullOrEmpty(SelectedLibraryPlaylist)) return;

        if (SavedLibraryPlaylists.Count <= 1)
        {
            await Jukebox.Views.ThreeButtonDialogView.ShowErrorAsync(
                "Delete Playlist",
                "Cannot delete the last remaining library playlist. You must have at least one.");
            return;
        }

        bool confirm = await Jukebox.Views.ThreeButtonDialogView.ShowConfirmAsync(
            "Delete Library Playlist",
            $"Are you sure you want to delete the playlist '{SelectedLibraryPlaylist}'?");
        if (!confirm) return;

        try
        {
            var filePath = Path.Combine(PlaylistsDirectory, "Library", $"{SelectedLibraryPlaylist}.json");
            if (File.Exists(filePath)) File.Delete(filePath);

            var toRemove = SelectedLibraryPlaylist;
            SavedLibraryPlaylists.Remove(toRemove);

            SelectedLibraryPlaylist = SavedLibraryPlaylists[0];
            await SaveActivePlaylistNameAsync(SelectedLibraryPlaylist, isRadio: false);
        }
        catch (Exception ex)
        {
            await Jukebox.Views.ThreeButtonDialogView.ShowErrorAsync("Delete Playlist Error", $"Could not delete playlist:\n{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteRadioPlaylistAsync()
    {
        if (string.IsNullOrEmpty(SelectedRadioPlaylist)) return;

        if (SavedRadioPlaylists.Count <= 1)
        {
            await Jukebox.Views.ThreeButtonDialogView.ShowErrorAsync(
                "Delete Playlist",
                "Cannot delete the last remaining radio playlist. You must have at least one.");
            return;
        }

        bool confirm = await Jukebox.Views.ThreeButtonDialogView.ShowConfirmAsync(
            "Delete Radio Playlist",
            $"Are you sure you want to delete the playlist '{SelectedRadioPlaylist}'?");
        if (!confirm) return;

        try
        {
            var filePath = Path.Combine(PlaylistsDirectory, "Radio", $"{SelectedRadioPlaylist}.json");
            if (File.Exists(filePath)) File.Delete(filePath);

            var toRemove = SelectedRadioPlaylist;
            SavedRadioPlaylists.Remove(toRemove);

            SelectedRadioPlaylist = SavedRadioPlaylists[0];
            await SaveActivePlaylistNameAsync(SelectedRadioPlaylist, isRadio: true);
        }
        catch (Exception ex)
        {
            await Jukebox.Views.ThreeButtonDialogView.ShowErrorAsync("Delete Playlist Error", $"Could not delete playlist:\n{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ClearLibraryPlaylistAsync()
    {
        bool confirm = await Jukebox.Views.ThreeButtonDialogView.ShowConfirmAsync(
            "Clear Library Playlist",
            "Are you sure you want to clear all tracks in the library playlist?");
        if (!confirm) return;

        InvalidatePlaylist();
        var libraryTracks = Playlist.Where(t => !IsUrlTrack(t)).ToList();
        foreach (var track in libraryTracks)
        {
            Playlist.Remove(track);
        }
        HasMultipleTracks = Playlist.Count > 1;
        UpdatePlaylistSummary();
        PlaylistCleared?.Invoke(this, EventArgs.Empty);

        await AutoSaveCurrentPlaylistAsync();
    }

    [RelayCommand]
    private async Task ClearRadioPlaylistAsync()
    {
        bool confirm = await Jukebox.Views.ThreeButtonDialogView.ShowConfirmAsync(
            "Clear Radio Playlist",
            "Are you sure you want to clear all stations in the radio playlist?");
        if (!confirm) return;

        InvalidatePlaylist();
        var radioTracks = Playlist.Where(t => IsUrlTrack(t)).ToList();
        foreach (var track in radioTracks)
        {
            Playlist.Remove(track);
        }
        HasMultipleTracks = Playlist.Count > 1;
        UpdatePlaylistSummary();
        PlaylistCleared?.Invoke(this, EventArgs.Empty);

        await AutoSaveCurrentPlaylistAsync();
    }

    [RelayCommand]
    private async Task RemoveLibrarySelectedAsync(System.Collections.IList? selectedItems)
    {
        if (selectedItems == null) return;
        InvalidatePlaylist();
        bool removedPlayingTrack = false;
        foreach (var item in selectedItems.Cast<JukeboxTrack>().ToList())
        {
            if (item.IsPlaying) removedPlayingTrack = true;
            Playlist.Remove(item);
        }

        HasMultipleTracks = Playlist.Count > 1;
        UpdatePlaylistSummary();

        if (removedPlayingTrack)
            PlayingTrackRemoved?.Invoke(this, EventArgs.Empty);

        int sv = ++_scrollVersion;
        TagVisibleRangeAsync(_pendingFirst, _pendingLast, _playlistVersion, sv).SafeFireAndForget(nameof(TagVisibleRangeAsync));

        await AutoSaveCurrentPlaylistAsync();
    }

    [RelayCommand]
    private async Task RemoveRadioSelectedAsync(System.Collections.IList? selectedItems)
    {
        if (selectedItems == null) return;
        InvalidatePlaylist();
        bool removedPlayingTrack = false;
        foreach (var item in selectedItems.Cast<JukeboxTrack>().ToList())
        {
            if (item.IsPlaying) removedPlayingTrack = true;
            Playlist.Remove(item);
        }

        HasMultipleTracks = Playlist.Count > 1;
        UpdatePlaylistSummary();

        if (removedPlayingTrack)
            PlayingTrackRemoved?.Invoke(this, EventArgs.Empty);

        int sv = ++_scrollVersion;
        TagVisibleRangeAsync(_pendingFirst, _pendingLast, _playlistVersion, sv).SafeFireAndForget(nameof(TagVisibleRangeAsync));

        await AutoSaveCurrentPlaylistAsync();
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

    private async Task FetchUrlMetadataAsync(JukeboxTrack track)
    {
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                track.DisplayName = track.FilePath;
                track.Length = TimeSpan.Zero;
                UpdatePlaylistSummary();
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[JukeboxPlaylistViewModel] Error fetching URL metadata: {ex.Message}");
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                track.DisplayName = track.FilePath;
            });
        }
        await Task.CompletedTask;
    }

    private async Task TagVisibleRangeAsync(int first, int last, int version, int scrollVersion)
    {
        if (Playlist.Count <= Constants.TagAllThreshold)
        {
            first = 0;
            last = Playlist.Count - 1;
        }
        else
        {
            first = Math.Max(0, first);
        }

        for (int batchStart = first; batchStart <= last; batchStart += Constants.TagBatchSize)
        {
            if (_playlistVersion != version) return;
            if (_scrollVersion != scrollVersion) return;

            int batchEnd = Math.Min(batchStart + Constants.TagBatchSize - 1, Math.Min(last, Playlist.Count - 1));

            var toTag = new List<(int index, JukeboxTrack track)>();
            for (int i = batchStart; i <= batchEnd; i++)
            {
                if (i < Playlist.Count && !Playlist[i].IsTagged)
                    toTag.Add((i, Playlist[i]));
            }

            if (toTag.Count == 0) continue;

            foreach (var (_, track) in toTag)
                track.IsTagged = true;

            var results = await Task.Run(() =>
                toTag.Select(t => (t.index, t.track, tags: ReadTags(t.track.FilePath))).ToList()
            );

            if (_playlistVersion != version) return;

            foreach (var (index, track, tags) in results)
            {
                if (index >= Playlist.Count || Playlist[index] != track) continue;
                track.DisplayName = tags.title;
                track.Length = tags.length;
                track.Bitrate = tags.bitrate;
            }

            if (!string.IsNullOrWhiteSpace(SearchLibraryText) || !string.IsNullOrWhiteSpace(SearchRadioText))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    FilteredLibraryPlaylist.Refresh();
                    FilteredRadioPlaylist.Refresh();
                });
            }
        }

        if (_playlistVersion == version && _scrollVersion == scrollVersion)
            UpdatePlaylistSummary();
    }

    private void InvalidatePlaylist()
    {
        _playlistVersion++;
        _scrollVersion++;
    }

    // Resolves a country input (which may be an ISO 3166-1 alpha-2 code from
    // radio-browser, or a free-form country name from a saved playlist) to a
    // short, display-friendly country name.
    //
    // Two input shapes are handled:
    //   1. ISO alpha-2 code ("US", "GB", "RU") — looked up via CountryNames.
    //      Returns "United States", "United Kingdom", "Russia", etc.
    //   2. Free-form string ("The Netherlands", "United States of America") —
    //      returned as-is. Saved playlists may contain these from older
    //      Jukebox versions that stored the radio-browser "country" field
    //      verbatim. Re-normalizing them would require a reverse lookup; we
    //      accept the minor inconsistency rather than risk corrupting user
    //      data on load.
    //
    // Returns "—" for null/empty/whitespace input (matches the existing UI
    // convention for missing metadata).
    private static string ResolveCountryName(string? country)
    {
        if (string.IsNullOrWhiteSpace(country))
            return "—";

        string trimmed = country.Trim();

        // ISO 3166-1 alpha-2 code: exactly 2 letters. Use CountryNames lookup
        // for clean short names ("US" → "United States", not "US").
        if (trimmed.Length == 2 && char.IsLetter(trimmed[0]) && char.IsLetter(trimmed[1]))
        {
            return CountryNames.GetShortName(trimmed);
        }

        // Free-form name (legacy saved playlist data) — return as-is.
        return trimmed;
    }

    private static List<string> DiscoverFiles(List<string> paths, bool noRecurse)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                try
                {
                    var options = new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = !noRecurse
                    };

                    foreach (var file in Directory.EnumerateFiles(path, "*.*", options))
                    {
                        string ext = Path.GetExtension(file);
                        if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            ExtractZipEntries(file, files);
                        }
                        else if (Constants.SupportedMediaExtensions.Contains(ext))
                        {
                            files.Add(file);
                        }
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"DiscoverFiles Error ({path}): {ex.Message}"); }
            }
            else if (File.Exists(path))
            {
                string ext = Path.GetExtension(path);
                if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractZipEntries(path, files);
                }
                else if (Constants.SupportedMediaExtensions.Contains(ext))
                {
                    files.Add(path);
                }
            }
        }
        return files;
    }

    private static void ExtractZipEntries(string zipPath, List<string> files)
    {
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                string entryExt = Path.GetExtension(entry.FullName);
                if (entryExt.Equals(".vgm", StringComparison.OrdinalIgnoreCase) ||
                    entryExt.Equals(".vgz", StringComparison.OrdinalIgnoreCase) ||
                    entryExt.Equals(".vgx", StringComparison.OrdinalIgnoreCase))
                {
                    files.Add($"{zipPath}|{entry.FullName}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Zip Read Error ({zipPath}): {ex.Message}");
        }
    }

    private static (string title, TimeSpan length, string bitrate) ReadTags(string filePath)
    {
        if (filePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            filePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return (filePath, TimeSpan.Zero, "Network Stream");
        }

        string ext;
        string displayName;
        if (filePath.Contains('|'))
        {
            var parts = filePath.Split('|', 2);
            ext = Path.GetExtension(parts[1]);
            string zipName = Path.GetFileNameWithoutExtension(parts[0]);
            displayName = $"{Path.GetFileNameWithoutExtension(parts[1])} [{zipName}]";
        }
        else
        {
            ext = Path.GetExtension(filePath);
            displayName = Path.GetFileNameWithoutExtension(filePath);
        }

        if (ext.Equals(".vgm", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".vgz", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".vgx", StringComparison.OrdinalIgnoreCase))
        {
            double durationMs = Jukebox.Services.VgmPlaybackEngine.GetVgmDurationMs(filePath);
            return (displayName, TimeSpan.FromMilliseconds(durationMs), "-");
        }

        try
        {
            using var tfile = TagLib.File.Create(filePath);
            var title = !string.IsNullOrWhiteSpace(tfile.Tag.Title)
                ? tfile.Tag.Title
                : displayName;
            var duration = tfile.Properties.Duration;
            var bitrate = tfile.Properties.AudioBitrate;
            return (title, duration, $"{bitrate} kbps");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TagLib Read Error ({filePath}): {ex.Message}");
            return (displayName, TimeSpan.Zero, "Unknown");
        }
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

    private async Task SwitchLibraryPlaylistAsync(string? oldName, string? newName)
    {
        _isSwitchingPlaylist = true;
        try
        {
            if (!string.IsNullOrEmpty(oldName))
            {
                await SavePlaylistToFileInternalAsync(oldName, isRadio: false);
            }

            var libraryTracks = Playlist.Where(t => !IsUrlTrack(t)).ToList();
            foreach (var track in libraryTracks)
            {
                Playlist.Remove(track);
            }

            if (!string.IsNullOrEmpty(newName))
            {
                var tracks = await LoadPlaylistFromFileInternalAsync(newName, isRadio: false);
                foreach (var track in tracks)
                {
                    Playlist.Add(track);
                }
                await SaveActivePlaylistNameAsync(newName, isRadio: false);
            }

            HasMultipleTracks = Playlist.Count > 1;
            UpdatePlaylistSummary();
        }
        finally
        {
            _isSwitchingPlaylist = false;
        }
    }

    private async Task SwitchRadioPlaylistAsync(string? oldName, string? newName)
    {
        _isSwitchingPlaylist = true;
        try
        {
            if (!string.IsNullOrEmpty(oldName))
            {
                await SavePlaylistToFileInternalAsync(oldName, isRadio: true);
            }

            var radioTracks = Playlist.Where(t => IsUrlTrack(t)).ToList();
            foreach (var track in radioTracks)
            {
                Playlist.Remove(track);
            }

            if (!string.IsNullOrEmpty(newName))
            {
                var tracks = await LoadPlaylistFromFileInternalAsync(newName, isRadio: true);
                foreach (var track in tracks)
                {
                    Playlist.Add(track);
                }
                await SaveActivePlaylistNameAsync(newName, isRadio: true);
            }

            HasMultipleTracks = Playlist.Count > 1;
            UpdatePlaylistSummary();
        }
        finally
        {
            _isSwitchingPlaylist = false;
        }
    }

    private async Task SavePlaylistToFileInternalAsync(string name, bool isRadio)
    {
        try
        {
            var folder = Path.Combine(PlaylistsDirectory, isRadio ? "Radio" : "Library");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            var filePath = Path.Combine(folder, $"{name}.json");
            // Transient tracks (browser preview slots) are never persisted to disk.
            var filteredTracks = Playlist.Where(t => IsUrlTrack(t) == isRadio && !t.IsTransient).ToList();

            var dto = new SavedPlaylistDto
            {
                Name = name,
                Tracks = filteredTracks.Select(t => new SavedTrackDto
                {
                    DisplayName = t.DisplayName,
                    FilePath = t.FilePath,
                    Length = t.Length,
                    Bitrate = t.Bitrate,
                    Genre = t.Genre,
                    Country = t.Country,
                    IsTagged = t.IsTagged
                }).ToList()
            };

            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Playlist] Save failed: {ex.Message}");
        }
    }

    private async Task<List<JukeboxTrack>> LoadPlaylistFromFileInternalAsync(string name, bool isRadio)
    {
        try
        {
            var folder = Path.Combine(PlaylistsDirectory, isRadio ? "Radio" : "Library");
            var filePath = Path.Combine(folder, $"{name}.json");
            if (!File.Exists(filePath)) return new List<JukeboxTrack>();

            var json = await File.ReadAllTextAsync(filePath);
            var dto = JsonSerializer.Deserialize<SavedPlaylistDto>(json);
            if (dto?.Tracks == null) return new List<JukeboxTrack>();

            return dto.Tracks.Select(t => new JukeboxTrack
            {
                DisplayName = t.DisplayName,
                FilePath = t.FilePath,
                Length = t.Length,
                Bitrate = t.Bitrate,
                Genre = t.Genre,
                Country = string.IsNullOrWhiteSpace(t.Country) ? "—" : t.Country.ToUpperInvariant().Trim(),
                IsTagged = t.IsTagged
            }).ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Playlist] Load failed: {ex.Message}");
            return new List<JukeboxTrack>();
        }
    }

    private async Task SaveActivePlaylistNameAsync(string name, bool isRadio)
    {
        try
        {
            var file = isRadio ? ActiveRadioPlaylistStateFile : ActiveLibraryPlaylistStateFile;
            await File.WriteAllTextAsync(file, name);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Playlist] Save active state failed: {ex.Message}");
        }
    }

    private async Task<string> LoadActivePlaylistNameAsync(bool isRadio)
    {
        try
        {
            var file = isRadio ? ActiveRadioPlaylistStateFile : ActiveLibraryPlaylistStateFile;
            if (File.Exists(file))
            {
                var name = await File.ReadAllTextAsync(file);
                return name.Trim();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Playlist] Load active state failed: {ex.Message}");
        }
        return "Default";
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
