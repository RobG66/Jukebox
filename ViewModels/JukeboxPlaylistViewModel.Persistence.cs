using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jukebox.Extensions;
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

// Saved-playlist persistence: on-disk load/save, delete, clear, switch active
// playlist. See JukeboxPlaylistViewModel.cs for the file-splitting overview.
public partial class JukeboxPlaylistViewModel
{
    #region Path Constants
    private string PlaylistsDirectory => Jukebox.Services.PathProvider.Current.PlaylistsDirectory;
    #endregion

    #region Startup & Auto-Save
    public async Task InitializeAsync()
    {
        await RunLibraryPlaylistOperationAsync(async () =>
        {
            try
            {
                var libraryFolder = Path.Combine(PlaylistsDirectory, "Library");
                if (!Directory.Exists(libraryFolder)) Directory.CreateDirectory(libraryFolder);

                // Scan for saved library playlists.
                SavedLibraryPlaylists.Clear();
                foreach (var file in Directory.GetFiles(libraryFolder, "*.json"))
                {
                    SavedLibraryPlaylists.Add(Path.GetFileNameWithoutExtension(file));
                }

                if (SavedLibraryPlaylists.Count == 0)
                {
                    await SaveTracksToFileInternalAsync("Default", Array.Empty<JukeboxTrack>());
                    SavedLibraryPlaylists.Add("Default");
                }

                // Startup playlist: "Default" when available, otherwise the first
                // persisted playlist. The saved-playlist page is never left in an
                // invalid no-selection state.
                _isSwitchingPlaylist = true;
                try
                {
                    SelectedLibraryPlaylist = SavedLibraryPlaylists.Contains("Default")
                        ? "Default"
                        : SavedLibraryPlaylists[0];
                }
                finally
                {
                    _isSwitchingPlaylist = false;
                }

                // Load the selected playlist (or leave empty if transient).
                string? startupPlaylist = SelectedLibraryPlaylist;
                LibraryPlaylist.Clear();

                if (!string.IsNullOrEmpty(startupPlaylist))
                {
                    var libraryTracks = await LoadPlaylistFromFileInternalAsync(startupPlaylist);
                    foreach (var track in libraryTracks) LibraryPlaylist.Add(track);
                }

                _loadedLibraryPlaylist = startupPlaylist;
                UpdatePlaylistSummary();
                await LoadPlaybackHistoryAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Playlist] InitializeAsync failed: {ex.Message}");
            }
        });
    }

    public Task AutoSaveCurrentPlaylistAsync() =>
        RunLibraryPlaylistOperationAsync(SaveLoadedLibraryPlaylistAsync);

    private Task SaveLoadedLibraryPlaylistAsync() =>
        string.IsNullOrEmpty(_loadedLibraryPlaylist)
            ? Task.CompletedTask
            : SavePlaylistToFileInternalAsync(_loadedLibraryPlaylist);

    /// <summary>Waits for all history writes requested before shutdown.</summary>
    public Task FlushPlaybackHistoryAsync() => _historySaveTask;
    #endregion

    #region Commands
    private static readonly string[] ReservedPlaylistNames = { "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3", "LPT4" };

    private (bool IsValid, string ErrorMessage) ValidatePlaylistName(string val)
    {
        if (string.IsNullOrWhiteSpace(val))
            return (false, "Name cannot be empty.");
        var invalid = Path.GetInvalidFileNameChars();
        if (val.Any(c => invalid.Contains(c)))
            return (false, "Name contains invalid characters.");
        if (ReservedPlaylistNames.Contains(val.ToUpperInvariant()))
            return (false, "Name is reserved by the file system.");
        return (true, string.Empty);
    }

    [RelayCommand]
    private async Task SaveLibraryPlaylistAsAsync()
    {
        await RunLibraryPlaylistOperationAsync(async () =>
        {
            var (name, _) = await _dialogService.ShowTextInputAsync(
                "Save Library Playlist As",
                "Enter playlist name:",
                placeholder: SelectedLibraryPlaylist ?? "",
                validator: ValidatePlaylistName,
                okButtonText: "Save",
                showDefaultCheckbox: true
            );

            if (string.IsNullOrWhiteSpace(name)) return;

            var filePath = Path.Combine(PlaylistsDirectory, "Library", $"{name}.json");
            if (File.Exists(filePath))
            {
                bool confirm = await _dialogService.ShowConfirmAsync(
                    "Overwrite Playlist",
                    $"A playlist named '{name}' already exists. Overwrite it?");
                if (!confirm) return;
            }

            await SavePlaylistToFileInternalAsync(name);

            if (!SavedLibraryPlaylists.Contains(name))
                SavedLibraryPlaylists.Add(name);

            SelectedLibraryPlaylist = name;
        });
    }

    [RelayCommand]
    private async Task NewLibraryPlaylistAsync()
    {
        await RunLibraryPlaylistOperationAsync(async () =>
        {
            var (name, _) = await _dialogService.ShowTextInputAsync(
                "New Playlist",
                "Enter playlist name:",
                placeholder: "",
                validator: ValidatePlaylistName,
                okButtonText: "Create",
                showDefaultCheckbox: false
            );

            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();

            var filePath = Path.Combine(PlaylistsDirectory, "Library", $"{name}.json");
            if (File.Exists(filePath))
            {
                bool loadExisting = await _dialogService.ShowConfirmAsync(
                    "Playlist Already Exists",
                    $"A playlist named '{name}' already exists. Load it instead?");

                if (loadExisting)
                {
                    SelectedLibraryPlaylist = name;
                }

                return;
            }

            await SaveTracksToFileInternalAsync(name, Array.Empty<JukeboxTrack>());
            if (!SavedLibraryPlaylists.Contains(name))
            {
                SavedLibraryPlaylists.Add(name);
            }

            SelectedLibraryPlaylist = name;
        });
    }

    [RelayCommand]
    private async Task SavePlayQueueAsPlaylistAsync()
    {
        await RunLibraryPlaylistOperationAsync(async () =>
        {
            if (PlayQueue.Count == 0)
            {
                await _dialogService.ShowErrorAsync(
                    "Save Queue",
                    "The play queue is empty.");
                return;
            }

            var (name, _) = await _dialogService.ShowTextInputAsync(
                "Save Queue As Playlist",
                "Enter playlist name:",
                placeholder: "",
                validator: ValidatePlaylistName,
                okButtonText: "Save",
                showDefaultCheckbox: true
            );

            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();

            var filePath = Path.Combine(PlaylistsDirectory, "Library", $"{name}.json");
            if (File.Exists(filePath))
            {
                bool overwrite = await _dialogService.ShowConfirmAsync(
                    "Overwrite Playlist",
                    $"A playlist named '{name}' already exists. Overwrite it?");
                if (!overwrite) return;
            }

            var tracks = PlayQueue.Select(CopyTrack).ToList();

            await SaveTracksToFileInternalAsync(name, tracks);
            if (!SavedLibraryPlaylists.Contains(name))
            {
                SavedLibraryPlaylists.Add(name);
            }
        }, skipWhenBusy: false);
    }

    [RelayCommand]
    private async Task DeleteLibraryPlaylistAsync()
    {
        await RunLibraryPlaylistOperationAsync(async () =>
        {
            if (string.IsNullOrEmpty(_loadedLibraryPlaylist)) return;

            bool confirm = await _dialogService.ShowConfirmAsync(
                "Delete Library Playlist",
                $"Are you sure you want to delete the playlist '{_loadedLibraryPlaylist}'?");
            if (!confirm) return;

            try
            {
                var toRemove = _loadedLibraryPlaylist;
                var filePath = Path.Combine(PlaylistsDirectory, "Library", $"{toRemove}.json");
                if (File.Exists(filePath)) File.Delete(filePath);

                string nextName = SelectedLibraryPlaylist != toRemove &&
                    SelectedLibraryPlaylist != null && SavedLibraryPlaylists.Contains(SelectedLibraryPlaylist)
                        ? SelectedLibraryPlaylist
                        : SavedLibraryPlaylists.FirstOrDefault(name => name != toRemove) ?? "Default";
                if (SavedLibraryPlaylists.Count == 1)
                    await SaveTracksToFileInternalAsync(nextName, Array.Empty<JukeboxTrack>());

                // Suppress only our synchronous selection change. Any later
                // request must queue normally while the replacement is loading.
                _isSwitchingPlaylist = true;
                try
                {
                    SavedLibraryPlaylists.Remove(toRemove);
                    if (!SavedLibraryPlaylists.Contains(nextName)) SavedLibraryPlaylists.Add(nextName);
                    SelectedLibraryPlaylist = nextName;
                }
                finally { _isSwitchingPlaylist = false; }

                var tracks = await LoadPlaylistFromFileInternalAsync(nextName);
                LibraryPlaylist.Clear();
                foreach (var track in tracks) LibraryPlaylist.Add(track);
                _loadedLibraryPlaylist = nextName;
                InvalidatePlaylist();
                UpdatePlaylistSummary();
            }
            catch (Exception ex)
            {
                await _dialogService.ShowErrorAsync("Delete Playlist Error", $"Could not delete playlist:\n{ex.Message}");
            }
        });
    }

    [RelayCommand]
    private async Task ClearLibraryPlaylistAsync()
    {
        await RunLibraryPlaylistOperationAsync(async () =>
        {
            bool confirm = await _dialogService.ShowConfirmAsync(
                "Clear Library Playlist",
                "Are you sure you want to clear all tracks in the library playlist?");
            if (!confirm) return;

            InvalidatePlaylist();
            LibraryPlaylist.Clear();
            UpdatePlaylistSummary();

            await SaveLoadedLibraryPlaylistAsync();
        });
    }

    [RelayCommand]
    private async Task RemoveLibrarySelectedAsync(System.Collections.IList? selectedItems)
    {
        await RunLibraryPlaylistOperationAsync(async () =>
        {
            if (selectedItems == null) return;
            InvalidatePlaylist();
            foreach (var item in selectedItems.Cast<JukeboxTrack>().ToList())
            {
                LibraryPlaylist.Remove(item);
            }

            UpdatePlaylistSummary();

            int sv = ++_scrollVersion;
            TagVisibleRangeAsync(_pendingFirst, _pendingLast, _playlistVersion, sv).SafeFireAndForget(nameof(TagVisibleRangeAsync));

            await SaveLoadedLibraryPlaylistAsync();
        });
    }

    [RelayCommand]
    private async Task RenameLibrarySelectedAsync(System.Collections.IList? selectedItems)
    {
        await RunLibraryPlaylistOperationAsync(async () =>
        {
            var track = selectedItems?.Cast<JukeboxTrack>().FirstOrDefault() ?? LibraryPlaylist.FirstOrDefault(t => t.IsSelected);
            if (track == null) return;

            string? newName = await _dialogService.ShowRenameAsync(
                track.DisplayName,
                title: "Rename Track",
                prompt: "Enter new title for this track:",
                isFileName: false);

            if (!string.IsNullOrWhiteSpace(newName) && !string.Equals(newName, track.DisplayName, StringComparison.Ordinal))
            {
                track.DisplayName = newName.Trim();
                InvalidatePlaylist();
                UpdatePlaylistSummary();
                await SaveLoadedLibraryPlaylistAsync();
            }
        });
    }

    [RelayCommand]
    private async Task MoveLibrarySelectedUpAsync(System.Collections.IList? selectedItems)
    {
        await RunLibraryPlaylistOperationAsync(async () =>
        {
            var selectedTracks = selectedItems?
                .Cast<object>()
                .OfType<JukeboxTrack>()
                .Distinct()
                .ToList()
                ?? new List<JukeboxTrack>();

            if (!MoveTracks(LibraryPlaylist, selectedTracks, -1))
            {
                return;
            }

            InvalidatePlaylist();
            FilteredLibraryPlaylist.Refresh();
            UpdatePlaylistSummary();
            await SaveLoadedLibraryPlaylistAsync();
        });
    }

    [RelayCommand]
    private async Task MoveLibrarySelectedDownAsync(System.Collections.IList? selectedItems)
    {
        await RunLibraryPlaylistOperationAsync(async () =>
        {
            var selectedTracks = selectedItems?
                .Cast<object>()
                .OfType<JukeboxTrack>()
                .Distinct()
                .ToList()
                ?? new List<JukeboxTrack>();

            if (!MoveTracks(LibraryPlaylist, selectedTracks, 1))
            {
                return;
            }

            InvalidatePlaylist();
            FilteredLibraryPlaylist.Refresh();
            UpdatePlaylistSummary();
            await SaveLoadedLibraryPlaylistAsync();
        });
    }

    [RelayCommand]
    private async Task CopySelectedToPlaylistAsync(CopyToPlaylistRequest? request)
    {
        await RunLibraryPlaylistOperationAsync(async () =>
        {
            if (request is null ||
                string.IsNullOrWhiteSpace(request.TargetPlaylist) ||
                request.Tracks.Count == 0 ||
                !SavedLibraryPlaylists.Contains(request.TargetPlaylist))
            {
                return;
            }

            var copies = request.Tracks.Select(CopyTrack).ToList();

            if (copies.Count == 0)
            {
                return;
            }

            if (string.Equals(
                    request.TargetPlaylist,
                    _loadedLibraryPlaylist,
                    StringComparison.OrdinalIgnoreCase))
            {
                InvalidatePlaylist();
                foreach (var copy in copies)
                {
                    LibraryPlaylist.Add(copy);
                }

                UpdatePlaylistSummary();
                await SaveLoadedLibraryPlaylistAsync();
                return;
            }

            var targetTracks = await LoadPlaylistFromFileInternalAsync(request.TargetPlaylist);
            targetTracks.AddRange(copies);
            await SaveTracksToFileInternalAsync(request.TargetPlaylist, targetTracks);
        }, skipWhenBusy: false);
    }
    #endregion

    #region Switch Active Playlist
    private async Task RunLibraryPlaylistOperationAsync(Func<Task> operation, bool skipWhenBusy = true)
    {
        // Ignore stale edit commands from a grid whose destination is changing.
        if (skipWhenBusy && IsLibraryPlaylistBusy) return;

        BeginLibraryPlaylistOperation();
        await _libraryPlaylistSwitchGate.WaitAsync();
        try
        {
            await operation();
        }
        finally
        {
            _libraryPlaylistSwitchGate.Release();
            EndLibraryPlaylistOperation();
        }
    }

    private void BeginLibraryPlaylistOperation()
    {
        _libraryPlaylistOperationCount++;
        IsLibraryPlaylistBusy = true;
    }

    private void EndLibraryPlaylistOperation()
    {
        IsLibraryPlaylistBusy = --_libraryPlaylistOperationCount > 0;
    }

    private async Task SwitchLibraryPlaylistAsync(string? newName)
    {
        BeginLibraryPlaylistOperation();
        await _libraryPlaylistSwitchGate.WaitAsync();
        try
        {
            // Skip requests superseded while another operation owned the gate.
            if (!string.Equals(newName, SelectedLibraryPlaylist, StringComparison.Ordinal)) return;

            InvalidatePlaylist();
            await SaveLoadedLibraryPlaylistAsync();
            var tracks = string.IsNullOrEmpty(newName)
                ? new List<JukeboxTrack>()
                : await LoadPlaylistFromFileInternalAsync(newName);

            LibraryPlaylist.Clear();
            foreach (var track in tracks) LibraryPlaylist.Add(track);
            _loadedLibraryPlaylist = newName;
            UpdatePlaylistSummary();
        }
        finally
        {
            _libraryPlaylistSwitchGate.Release();
            EndLibraryPlaylistOperation();
        }
    }
    #endregion

    #region Internal Save/Load
    private async Task SavePlaylistToFileInternalAsync(string name)
        => await SaveTracksToFileInternalAsync(name, LibraryPlaylist);

    private async Task SaveTracksToFileInternalAsync(
        string name,
        IEnumerable<JukeboxTrack> sourceTracks)
    {
        try
        {
            var folder = Path.Combine(PlaylistsDirectory, "Library");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            var filePath = Path.Combine(folder, $"{name}.json");
            var tracks = sourceTracks.ToList();

            var dto = new SavedPlaylistDto
            {
                Name = name,
                Tracks = tracks.Select(t => new SavedTrackDto
                {
                    DisplayName = t.DisplayName,
                    FilePath = t.FilePath,
                    OriginalUrl = t.OriginalUrl,
                    SourcePluginId = t.SourcePluginId,
                    Length = t.Length,
                    Bitrate = t.Bitrate,
                    Genre = t.Genre,
                    Country = t.Country,
                    Location = t.Location,
                    IsTagged = t.IsTagged
                }).ToList()
            };

            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            await WriteTextAtomicallyAsync(filePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Playlist] Save failed: {ex.Message}");
        }
    }

    private string PlaybackHistoryFile =>
        Path.Combine(Jukebox.Services.PathProvider.Current.CacheDirectory, "PlaybackHistory.json");

    private async Task LoadPlaybackHistoryAsync()
    {
        RecentlyPlayed.Clear();

        try
        {
            if (!File.Exists(PlaybackHistoryFile))
            {
                UpdatePlaybackHistorySummary();
                return;
            }

            string json = await File.ReadAllTextAsync(PlaybackHistoryFile);
            var dto = JsonSerializer.Deserialize<PlaybackHistoryDto>(json);
            foreach (var entry in dto?.Entries.Take(MaxPlaybackHistoryEntries)
                         ?? Enumerable.Empty<PlaybackHistoryEntryDto>())
            {
                RecentlyPlayed.Add(new PlaybackHistoryEntry
                {
                    PlayedAt = entry.PlayedAt,
                    Track = FromSavedTrackDto(entry.Track)
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[History] Load failed: {ex.Message}");
        }

        UpdatePlaybackHistorySummary();
    }

    private async Task SavePlaybackHistoryAfterAsync(Task previousSave)
    {
        await previousSave;
        await SavePlaybackHistoryAsync();
    }

    private async Task SavePlaybackHistoryAsync()
    {
        await _historySaveGate.WaitAsync();
        try
        {
            var dto = new PlaybackHistoryDto
            {
                Entries = RecentlyPlayed.Select(entry => new PlaybackHistoryEntryDto
                {
                    PlayedAt = entry.PlayedAt,
                    Track = ToSavedTrackDto(entry.Track)
                }).ToList()
            };

            string json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            await WriteTextAtomicallyAsync(PlaybackHistoryFile, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[History] Save failed: {ex.Message}");
        }
        finally
        {
            _historySaveGate.Release();
        }
    }

    private static SavedTrackDto ToSavedTrackDto(JukeboxTrack track) => new()
    {
        DisplayName = track.DisplayName,
        FilePath = track.FilePath,
        OriginalUrl = track.OriginalUrl,
        SourcePluginId = track.SourcePluginId,
        Length = track.Length,
        Bitrate = track.Bitrate,
        Genre = track.Genre,
        Country = track.Country,
        Location = track.Location,
        IsTagged = track.IsTagged
    };

    private static JukeboxTrack FromSavedTrackDto(SavedTrackDto track) => new()
    {
        DisplayName = track.DisplayName,
        FilePath = track.FilePath,
        OriginalUrl = track.OriginalUrl,
        SourcePluginId = track.SourcePluginId,
        Length = track.Length,
        Bitrate = track.Bitrate,
        Genre = track.Genre,
        Country = track.Country ?? "—",
        Location = track.Location ?? "—",
        IsTagged = track.IsTagged
    };

    private static async Task WriteTextAtomicallyAsync(string filePath, string text)
    {
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, text);
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task<List<JukeboxTrack>> LoadPlaylistFromFileInternalAsync(string name)
    {
        try
        {
            var folder = Path.Combine(PlaylistsDirectory, "Library");
            var filePath = Path.Combine(folder, $"{name}.json");
            if (!File.Exists(filePath)) return new List<JukeboxTrack>();

            var json = await File.ReadAllTextAsync(filePath);
            var dto = JsonSerializer.Deserialize<SavedPlaylistDto>(json);
            if (dto?.Tracks == null) return new List<JukeboxTrack>();

            return dto.Tracks.Select(t => new JukeboxTrack
            {
                DisplayName = t.DisplayName,
                FilePath = t.FilePath,
                OriginalUrl = t.OriginalUrl,
                SourcePluginId = t.SourcePluginId,
                Length = t.Length,
                Bitrate = t.Bitrate,
                Genre = t.Genre,
                Country = t.Country ?? "—",
                Location = t.Location ?? "—",
                IsTagged = t.IsTagged
            }).ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Playlist] Load failed: {ex.Message}");
            return new List<JukeboxTrack>();
        }
    }
    #endregion
}

public sealed record CopyToPlaylistRequest(
    string TargetPlaylist,
    IReadOnlyList<JukeboxTrack> Tracks);
