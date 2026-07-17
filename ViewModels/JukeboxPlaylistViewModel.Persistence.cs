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
            LibraryPlaylist.Clear();

            if (!string.IsNullOrEmpty(SelectedLibraryPlaylist))
            {
                var libraryTracks = await LoadPlaylistFromFileInternalAsync(SelectedLibraryPlaylist);
                foreach (var track in libraryTracks) LibraryPlaylist.Add(track);
            }

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
            await SavePlaylistToFileInternalAsync(SelectedLibraryPlaylist);
        }
    }
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
    }

    [RelayCommand]
    private async Task NewLibraryPlaylistAsync()
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
    }

    [RelayCommand]
    private async Task SavePlayQueueAsPlaylistAsync()
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
            showDefaultCheckbox: false
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
    }

    [RelayCommand]
    private async Task DeleteLibraryPlaylistAsync()
    {
        if (string.IsNullOrEmpty(SelectedLibraryPlaylist)) return;

        bool confirm = await _dialogService.ShowConfirmAsync(
            "Delete Library Playlist",
            $"Are you sure you want to delete the playlist '{SelectedLibraryPlaylist}'?");
        if (!confirm) return;

        try
        {
            var toRemove = SelectedLibraryPlaylist;
            var filePath = Path.Combine(PlaylistsDirectory, "Library", $"{toRemove}.json");
            if (File.Exists(filePath)) File.Delete(filePath);

            _isSwitchingPlaylist = true;
            try
            {
                SavedLibraryPlaylists.Remove(toRemove);

                if (SavedLibraryPlaylists.Count > 0)
                {
                    SelectedLibraryPlaylist = SavedLibraryPlaylists[0];
                    var tracks = await LoadPlaylistFromFileInternalAsync(SelectedLibraryPlaylist);
                    LibraryPlaylist.Clear();
                    foreach (var track in tracks) LibraryPlaylist.Add(track);
                }
                else
                {
                    const string fallbackName = "Default";
                    await SaveTracksToFileInternalAsync(fallbackName, Array.Empty<JukeboxTrack>());
                    SavedLibraryPlaylists.Add(fallbackName);
                    SelectedLibraryPlaylist = fallbackName;
                    LibraryPlaylist.Clear();
                }

                UpdatePlaylistSummary();
            }
            finally
            {
                _isSwitchingPlaylist = false;
            }
        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync("Delete Playlist Error", $"Could not delete playlist:\n{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ClearLibraryPlaylistAsync()
    {
        bool confirm = await _dialogService.ShowConfirmAsync(
            "Clear Library Playlist",
            "Are you sure you want to clear all tracks in the library playlist?");
        if (!confirm) return;

        InvalidatePlaylist();
        LibraryPlaylist.Clear();
        UpdatePlaylistSummary();

        await AutoSaveCurrentPlaylistAsync();
    }

    [RelayCommand]
    private async Task RemoveLibrarySelectedAsync(System.Collections.IList? selectedItems)
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

        await AutoSaveCurrentPlaylistAsync();
    }

    [RelayCommand]
    private async Task CopySelectedToPlaylistAsync(CopyToPlaylistRequest? request)
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
                SelectedLibraryPlaylist,
                StringComparison.OrdinalIgnoreCase))
        {
            InvalidatePlaylist();
            foreach (var copy in copies)
            {
                LibraryPlaylist.Add(copy);
            }

            UpdatePlaylistSummary();
            await AutoSaveCurrentPlaylistAsync();
            return;
        }

        var targetTracks = await LoadPlaylistFromFileInternalAsync(request.TargetPlaylist);
        targetTracks.AddRange(copies);
        await SaveTracksToFileInternalAsync(request.TargetPlaylist, targetTracks);
    }
    #endregion

    #region Switch Active Playlist
    private async Task SwitchLibraryPlaylistAsync(string? oldName, string? newName)
    {
        _isSwitchingPlaylist = true;
        try
        {
            InvalidatePlaylist();

            if (!string.IsNullOrEmpty(oldName))
            {
                await SavePlaylistToFileInternalAsync(oldName);
            }

            LibraryPlaylist.Clear();

            if (!string.IsNullOrEmpty(newName))
            {
                var tracks = await LoadPlaylistFromFileInternalAsync(newName);
                foreach (var track in tracks)
                {
                    LibraryPlaylist.Add(track);
                }
            }

            UpdatePlaylistSummary();
        }
        finally
        {
            _isSwitchingPlaylist = false;
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
