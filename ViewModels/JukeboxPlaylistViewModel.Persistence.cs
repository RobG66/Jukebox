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

// Saved-playlist persistence: on-disk load/save, delete, clear, switch active
// playlist. See JukeboxPlaylistViewModel.cs for the file-splitting overview.
public partial class JukeboxPlaylistViewModel
{
    #region Path Constants
    private string PlaylistsDirectory => Path.Combine(Jukebox.Services.PathProvider.Current.SettingsDirectory, "Playlists");
    private string ActiveLibraryPlaylistStateFile => Path.Combine(Jukebox.Services.PathProvider.Current.SettingsDirectory, "ActiveLibraryPlaylist.txt");
    private string ActiveRadioPlaylistStateFile => Path.Combine(Jukebox.Services.PathProvider.Current.SettingsDirectory, "ActiveRadioPlaylist.txt");
    #endregion

    #region Startup & Auto-Save
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

    #region Switch Active Playlist
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
    #endregion

    #region Internal Save/Load
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
}
