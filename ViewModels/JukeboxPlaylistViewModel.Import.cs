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

// Turning input (file paths, zips, URLs, radio-browser metadata) into tagged
// JukeboxTrack entries. See JukeboxPlaylistViewModel.cs for the file-splitting overview.
public partial class JukeboxPlaylistViewModel
{
    #region Public Methods
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
    #endregion

    #region Tagging & Metadata
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
    #endregion

    #region File Discovery
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
    #endregion
}
