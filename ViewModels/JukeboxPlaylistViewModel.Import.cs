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

// Turning input (file paths, zips, URLs, radio-browser metadata) into tagged
// JukeboxTrack entries. See JukeboxPlaylistViewModel.cs for the file-splitting overview.
public partial class JukeboxPlaylistViewModel
{
    #region Public Methods
    public async Task<IReadOnlyList<JukeboxTrack>> ProcessAndAddFilesAsync(
        IEnumerable<string> paths,
        PlaylistTarget target,
        bool noRecurse = false)
    {
        ArgumentNullException.ThrowIfNull(paths);

        bool isQueueImport = target == PlaylistTarget.PlayQueue;
        if (isQueueImport)
        {
            // File discovery runs off the UI thread. Serialize queue imports
            // so a later drop cannot finish discovery first and appear ahead
            // of an earlier drop in the queue.
            await _playQueueImportGate.WaitAsync();
        }

        try
        {
            return await ProcessAndAddFilesCoreAsync(paths, target, noRecurse);
        }
        finally
        {
            if (isQueueImport)
            {
                _playQueueImportGate.Release();
            }
        }
    }

    private async Task<IReadOnlyList<JukeboxTrack>> ProcessAndAddFilesCoreAsync(
        IEnumerable<string> paths,
        PlaylistTarget target,
        bool noRecurse)
    {
        int version = _playlistVersion;
        if (target == PlaylistTarget.SelectedSavedPlaylist)
        {
            InvalidatePlaylist();
            version = _playlistVersion;
        }

        var requestedPaths = paths.ToList();
        var filePaths = await Task.Run(() => DiscoverFiles(requestedPaths, noRecurse));

        if (target == PlaylistTarget.SelectedSavedPlaylist &&
            _playlistVersion != version)
        {
            return Array.Empty<JukeboxTrack>();
        }

        var additions = filePaths.Select(CreateTrackFromPath).ToList();
        if (additions.Count == 0)
        {
            return additions;
        }

        if (target == PlaylistTarget.PlayQueue)
        {
            var queuedAdditions = AppendNewToPlayQueue(additions);
            TagImportedQueueTracksAsync(queuedAdditions)
                .SafeFireAndForget(nameof(TagImportedQueueTracksAsync));
            return queuedAdditions;
        }

        foreach (var track in additions)
        {
            LibraryPlaylist.Add(track);
        }

        UpdatePlaylistSummary();
        int sv = ++_scrollVersion;
        TagVisibleRangeAsync(_pendingFirst, _pendingLast, version, sv).SafeFireAndForget(nameof(TagVisibleRangeAsync));

        await AutoSaveCurrentPlaylistAsync();
        return additions;
    }

    private static JukeboxTrack CreateTrackFromPath(string path)
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

        return new JukeboxTrack
        {
            DisplayName = displayName,
            FilePath = path
        };
    }

    #endregion

    #region Tagging
    private async Task TagImportedQueueTracksAsync(IReadOnlyList<JukeboxTrack> tracks)
    {
        foreach (var track in tracks)
        {
            track.IsTagged = true;
        }

        var results = await Task.Run(() => tracks
            .Select(track => (track, tags: ReadTags(track.FilePath)))
            .ToList());

        foreach (var (track, tags) in results)
        {
            track.DisplayName = tags.title;
            track.Length = tags.length;
            track.Bitrate = tags.bitrate;
        }

        UpdatePlaylistSummary();
    }

    private async Task TagVisibleRangeAsync(int first, int last, int version, int scrollVersion)
    {
        if (LibraryPlaylist.Count <= Constants.TagAllThreshold)
        {
            first = 0;
            last = LibraryPlaylist.Count - 1;
        }
        else
        {
            first = Math.Max(0, first);
        }

        for (int batchStart = first; batchStart <= last; batchStart += Constants.TagBatchSize)
        {
            if (_playlistVersion != version) return;
            if (_scrollVersion != scrollVersion) return;

            int batchEnd = Math.Min(batchStart + Constants.TagBatchSize - 1, Math.Min(last, LibraryPlaylist.Count - 1));

            var toTag = new List<(int index, JukeboxTrack track)>();
            for (int i = batchStart; i <= batchEnd; i++)
            {
                if (i < LibraryPlaylist.Count && !LibraryPlaylist[i].IsTagged)
                    toTag.Add((i, LibraryPlaylist[i]));
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
                if (index >= LibraryPlaylist.Count || LibraryPlaylist[index] != track) continue;
                track.DisplayName = tags.title;
                track.Length = tags.length;
                track.Bitrate = tags.bitrate;
            }

            if (!string.IsNullOrWhiteSpace(SearchLibraryText))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => FilteredLibraryPlaylist.Refresh());
            }
        }

        if (_playlistVersion == version && _scrollVersion == scrollVersion)
            UpdatePlaylistSummary();
    }
    #endregion

    #region File Discovery
    private static List<string> DiscoverFiles(IEnumerable<string> paths, bool noRecurse)
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
    #endregion
}
