using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Jukebox.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Jukebox.Views;

public partial class JukeboxControl : UserControl
{
    public JukeboxControl()
    {
        InitializeComponent();
    }

    private async void AddFiles_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Media Files",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Media Files")
                {
                    Patterns = new[] { "*.mp3", "*.flac", "*.wav", "*.ogg", "*.m4a", "*.wma" }
                }
            }
        });

        if (files != null && files.Count > 0 && DataContext is JukeboxViewModel vm)
        {
            var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (paths.Count > 0)
            {
                await ProcessAndAddFilesAsync(paths!, vm);
            }
        }
    }

    private async void AddFolder_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Media Folder",
            AllowMultiple = false
        });

        if (folders != null && folders.Count > 0 && DataContext is JukeboxViewModel vm)
        {
            var folderPath = folders[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(folderPath)) return;

            var supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
            { 
                ".mp3", ".flac", ".wav", ".ogg", ".m4a", ".wma" 
            };

            var paths = await Task.Run(() => 
            {
                try
                {
                    return Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories)
                                    .Where(file => supportedExtensions.Contains(Path.GetExtension(file)))
                                    .ToList();
                }
                catch
                {
                    return new List<string>();
                }
            });

            if (paths.Count > 0)
            {
                await ProcessAndAddFilesAsync(paths, vm);
            }
        }
    }

    private async Task ProcessAndAddFilesAsync(List<string> filePaths, JukeboxViewModel vm)
    {
        var tracks = await Task.Run(() =>
        {
            var results = new List<JukeboxTrack>();
            foreach (var path in filePaths)
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
                vm.Playlist.Add(track);
            }
        });
    }

    private void PlaylistDataGrid_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (sender is DataGrid dataGrid && dataGrid.SelectedItem is JukeboxTrack selectedTrack)
        {
            if (DataContext is JukeboxViewModel vm)
            {
                vm.CurrentTrack = selectedTrack;
                
                // If we want to simulate playback start, we could call Play()
                if (vm.PlayCommand.CanExecute(null))
                {
                    vm.PlayCommand.Execute(null);
                }
            }
        }
    }
}
