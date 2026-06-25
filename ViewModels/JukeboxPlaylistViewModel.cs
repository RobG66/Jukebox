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
    // REFACTOR: magic numbers → Constants (smell §4.6, §6.4).
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
            Playlist.Add(new JukeboxTrack
            {
                DisplayName = Path.GetFileNameWithoutExtension(path),
                FilePath = path
            });
        }

        HasMultipleTracks = Playlist.Count > 1;
        UpdatePlaylistSummary();

        int sv = ++_scrollVersion;
        TagVisibleRangeAsync(_pendingFirst, _pendingLast, version, sv).SafeFireAndForget(nameof(TagVisibleRangeAsync));
    }

    private async Task TagVisibleRangeAsync(int first, int last, int version, int scrollVersion)
    {
        // REFACTOR: magic number 100 → Constants.TagAllThreshold (smell §4.6, §6.4).
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
                    // REFACTOR: add EnumerationOptions to skip inaccessible files
                    // (was smell §4.6 Warning: DiscoverFiles enumerates all files
                    // synchronously — minor enhancement, original enumeration still
                    // works but is now resilient to permission errors on Linux).
                    var options = new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = !noRecurse
                    };
                    files.AddRange(Directory.EnumerateFiles(path, "*.*", options)
                        .Where(f => Constants.SupportedMediaExtensions.Contains(Path.GetExtension(f)))
                    );
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"DiscoverFiles Error ({path}): {ex.Message}"); }
            }
            else if (File.Exists(path))
            {
                if (Constants.SupportedMediaExtensions.Contains(Path.GetExtension(path)))
                    files.Add(path);
            }
        }
        return files;
    }

    private static (string title, TimeSpan length, string bitrate) ReadTags(string filePath)
    {
        try
        {
            using var tfile = TagLib.File.Create(filePath);
            var title = !string.IsNullOrWhiteSpace(tfile.Tag.Title)
                ? tfile.Tag.Title
                : Path.GetFileNameWithoutExtension(filePath);
            var duration = tfile.Properties.Duration;
            var bitrate = tfile.Properties.AudioBitrate;
            return (title, duration, $"{bitrate} kbps");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TagLib Read Error ({filePath}): {ex.Message}");
            return (Path.GetFileNameWithoutExtension(filePath), TimeSpan.Zero, "Unknown");
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
