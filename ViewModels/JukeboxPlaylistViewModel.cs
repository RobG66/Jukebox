using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Jukebox.ViewModels;

public partial class JukeboxPlaylistViewModel : ViewModelBase
{
    public ObservableCollection<JukeboxTrack> Playlist { get; } = new();

    [ObservableProperty] private bool _hasMultipleTracks = false;

    public JukeboxPlaylistViewModel()
    {
        Playlist.CollectionChanged += (s, e) => 
        {
            HasMultipleTracks = Playlist.Count > 1;
        };
    }

    [RelayCommand]
    private void ClearPlaylist()
    {
        Playlist.Clear();
    }

    [RelayCommand]
    private void RemoveSelected(System.Collections.IList? selectedItems)
    {
        if (selectedItems == null) return;
        
        var itemsToRemove = selectedItems.Cast<JukeboxTrack>().ToList();
        foreach (var item in itemsToRemove)
        {
            Playlist.Remove(item);
        }
    }

    public async Task ProcessAndAddFilesAsync(List<string> paths, bool noRecurse = false)
    {
        var filesToProcess = new List<string>();

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var supportedExtensions = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) 
                { 
                    ".mp3", ".flac", ".wav", ".ogg", ".m4a", ".wma", ".mp4", ".mkv", ".avi", ".webm" 
                };

                try
                {
                    var dirFiles = Directory.GetFiles(path, "*.*", noRecurse ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories)
                                            .Where(file => supportedExtensions.Contains(Path.GetExtension(file)));
                    filesToProcess.AddRange(dirFiles);
                }
                catch { }
            }
            else if (File.Exists(path))
            {
                filesToProcess.Add(path);
            }
        }

        var tracks = await Task.Run(() =>
        {
            var results = new List<JukeboxTrack>();
            foreach (var path in filesToProcess)
            {
                try
                {
                    using var tfile = TagLib.File.Create(path);
                    var title = !string.IsNullOrWhiteSpace(tfile.Tag.Title) 
                                ? tfile.Tag.Title 
                                : Path.GetFileNameWithoutExtension(path);
                    
                    var duration = tfile.Properties.Duration;
                    var bitrate = tfile.Properties.AudioBitrate;
                    
                    results.Add(new JukeboxTrack
                    {
                        DisplayName = title,
                        Length = $"{(int)duration.TotalMinutes}:{duration.Seconds:D2}",
                        Bitrate = $"{bitrate} kbps",
                        FilePath = path
                    });
                }
                catch
                {
                    // Fallback if TagLib fails
                    results.Add(new JukeboxTrack
                    {
                        DisplayName = Path.GetFileNameWithoutExtension(path),
                        Length = "0:00",
                        Bitrate = "Unknown",
                        FilePath = path
                    });
                }
            }
            return results;
        });

        Dispatcher.UIThread.Post(() =>
        {
            foreach (var track in tracks)
            {
                Playlist.Add(track);
            }
        });
    }
}
