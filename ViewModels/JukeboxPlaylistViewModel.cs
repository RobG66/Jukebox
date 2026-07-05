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

namespace Jukebox.ViewModels;

public partial class JukeboxPlaylistViewModel : ViewModelBase
{
    private int _playlistVersion = 0;
    private int _scrollVersion = 0;
    private int _pendingFirst = 0;
    private int _pendingLast = Constants.TagBatchSize - 1;

    public ObservableCollection<JukeboxTrack> Playlist { get; } = new();
    public DataGridCollectionView FilteredPlaylist { get; }

    [ObservableProperty] private bool _hasMultipleTracks = false;
    [ObservableProperty] private string _playlistSummary = "0 Tracks | 0h 0m total";
    [ObservableProperty] private string _searchText = "";

    public JukeboxPlaylistViewModel()
    {
        FilteredPlaylist = new DataGridCollectionView(Playlist);
        FilteredPlaylist.Filter = FilterTrack;
    }

    partial void OnSearchTextChanged(string value)
    {
        FilteredPlaylist.Refresh();
    }

    private bool FilterTrack(object arg)
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        if (arg is JukeboxTrack track)
        {
            return track.DisplayName?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) == true;
        }
        return false;
    }

    public event EventHandler? PlaylistCleared;

    public void NotifyVisibleRange(int firstIndex, int lastIndex)
    {
        _pendingFirst = firstIndex;
        _pendingLast = lastIndex;
        int sv = ++_scrollVersion;
        TagVisibleRangeAsync(firstIndex, lastIndex, _playlistVersion, sv).SafeFireAndForget(nameof(TagVisibleRangeAsync));
    }

    [RelayCommand]
    private void ClearPlaylist()
    {
        InvalidatePlaylist();
        Playlist.Clear();
        HasMultipleTracks = false;
        UpdatePlaylistSummary();
        PlaylistCleared?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void RemoveSelected(System.Collections.IList? selectedItems)
    {
        if (selectedItems == null) return;
        InvalidatePlaylist();
        foreach (var item in selectedItems.Cast<JukeboxTrack>().ToList())
            Playlist.Remove(item);

        HasMultipleTracks = Playlist.Count > 1;
        UpdatePlaylistSummary();

        int sv = ++_scrollVersion;
        TagVisibleRangeAsync(_pendingFirst, _pendingLast, _playlistVersion, sv).SafeFireAndForget(nameof(TagVisibleRangeAsync));
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
    }

    public async Task AddUrlTrackAsync(string url)
    {
        InvalidatePlaylist();

        var track = new JukeboxTrack
        {
            DisplayName = "Loading Stream Title...",
            FilePath = url,
            IsTagged = true // Bypasses default lazy tagger
        };

        Playlist.Add(track);
        HasMultipleTracks = Playlist.Count > 1;
        UpdatePlaylistSummary();

        // Start background metadata fetch
        FetchUrlMetadataAsync(track).SafeFireAndForget(nameof(FetchUrlMetadataAsync));

        await Task.CompletedTask;
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
        // If the playlist is small, tag all items at once.
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

            // Mark as tagged immediately to prevent concurrent calls from processing the same tracks
            foreach (var (_, track) in toTag)
                track.IsTagged = true;

            var results = await Task.Run(() =>
                toTag.Select(t => (t.index, t.track, tags: ReadTags(t.track.FilePath))).ToList()
            );

            // Only _playlistVersion gates the write-back — scroll position is irrelevant
            // to whether the tags themselves are valid. Always commit completed reads.
            if (_playlistVersion != version) return;

            foreach (var (index, track, tags) in results)
            {
                if (index >= Playlist.Count || Playlist[index] != track) continue;
                track.DisplayName = tags.title;
                track.Length = tags.length;
                track.Bitrate = tags.bitrate;
            }

            // Refresh filter if tags were updated and search is active
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => FilteredPlaylist.Refresh());
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
        int count = Playlist.Count;
        double totalSeconds = 0;
        foreach (var track in Playlist)
        {
            totalSeconds += track.Length.TotalSeconds;
        }
        int hours = (int)totalSeconds / 3600;
        int minutes = ((int)totalSeconds % 3600) / 60;
        PlaylistSummary = $"{count} Track{(count != 1 ? "s" : "")} | {hours}h {minutes}m total";
    }
}
