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
using Avalonia.Input;

namespace Jukebox.Views;

public partial class JukeboxControl : UserControl
{
    private Avalonia.Threading.DispatcherTimer _inactivityTimer;

    public JukeboxControl()
    {
        InitializeComponent();
        
        _inactivityTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _inactivityTimer.Tick += (s, e) => 
        {
            if (DataContext is JukeboxViewModel vm && vm.IsAutoHideEnabled)
            {
                vm.ControlBarHeight = 0;
            }
            _inactivityTimer.Stop();
        };

        this.PointerMoved += (s, e) => ResetInactivity();
        this.PointerEntered += (s, e) => ResetInactivity();
        
        DataContextChanged += OnDataContextChanged;
        Loaded += (s, e) => 
        {
            ProjectMDisplay.StartEngine();
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel != null)
            {
                topLevel.AddHandler(InputElement.KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
            }
        };
        Unloaded += (s, e) => 
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel != null)
            {
                topLevel.RemoveHandler(InputElement.KeyDownEvent, OnPreviewKeyDown);
            }
        };
    }

    private void ResetInactivity()
    {
        if (DataContext is JukeboxViewModel vm)
        {
            vm.ControlBarHeight = 65;
            _inactivityTimer.Stop();
            if (vm.IsAutoHideEnabled)
            {
                _inactivityTimer.Start();
            }
        }
    }

    private void OnPreviewKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape)
        {
            if (DataContext is JukeboxViewModel vm)
            {
                bool handled = false;
                if (vm.IsPlaylistVisible) { vm.IsPlaylistVisible = false; handled = true; }
                if (vm.IsEqVisible) { vm.IsEqVisible = false; handled = true; }
                if (vm.IsPickerVisible) { vm.IsPickerVisible = false; handled = true; }
                if (handled) e.Handled = true;
            }
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is JukeboxViewModel vm)
        {
            vm.PcmDataAvailable += (s, buffer) => ProjectMDisplay.FeedPcm(buffer);
            vm.VisualizerViewModel.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName == nameof(JukeboxVisualizerViewModel.SelectedVisualizerPath) && !string.IsNullOrEmpty(vm.VisualizerViewModel.SelectedVisualizerPath))
                {
                    ProjectMDisplay.LoadPreset(vm.VisualizerViewModel.SelectedVisualizerPath);
                }
            };
        }
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
                vm.PlaylistViewModel.Playlist.Add(track);
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

    private void VisualizerTreeDataGrid_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is JukeboxViewModel vm && vm.VisualizerViewModel.VisualizerSource?.RowSelection?.SelectedItems?.FirstOrDefault() is VisualizerFileViewModel fileVm)
        {
            vm.VisualizerViewModel.SelectedVisualizerPath = fileVm.Path;
            ProjectMDisplay.LoadPreset(fileVm.Path);
        }
    }
}
