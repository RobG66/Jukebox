using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Selection;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;
using Avalonia.Controls;

namespace Jukebox.ViewModels;

public partial class JukeboxVisualizerViewModel : ViewModelBase
{
    [ObservableProperty] private string? _selectedVisualizerPath;
    
    public HierarchicalTreeDataGridSource<VisualizerNodeViewModel>? VisualizerSource { get; private set; }

    private List<string> _allVisualizerPaths = new();
    private DispatcherTimer _randomizerTimer;

    [ObservableProperty] private bool _isVisualizerRandomizerEnabled;
    [ObservableProperty] private int _visualizerRandomizerIntervalSeconds = 10;

    public JukeboxVisualizerViewModel()
    {
        _randomizerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(VisualizerRandomizerIntervalSeconds) };
        _randomizerTimer.Tick += RandomizerTimer_Tick;

        // Restore last visualizer from temp folder if it exists
        var tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectM", "temp_preset");
        if (Directory.Exists(tempDir))
        {
            var existingPresets = Directory.GetFiles(tempDir, "*.milk");
            if (existingPresets.Length > 0)
            {
                SelectedVisualizerPath = existingPresets[0];
            }
        }
    }

    public async Task LoadVisualizersAsync()
    {
        var rootFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectM");
        if (!Directory.Exists(rootFolder)) return;

        var rootNodes = new ObservableCollection<VisualizerNodeViewModel>();

        await Task.Run(() =>
        {
            var directories = Directory.GetDirectories(rootFolder);
            foreach (var dir in directories)
            {
                var folderName = Path.GetFileName(dir);
                if (folderName == "win-x64" || folderName == "textures") continue;

                var folderVm = new VisualizerFolderViewModel(folderName, dir);
                PopulateFolder(folderVm, dir);
                if (folderVm.Children.Count > 0)
                {
                    Dispatcher.UIThread.Invoke(() => rootNodes.Add(folderVm));
                }
            }
        });

        Dispatcher.UIThread.Post(() =>
        {
            VisualizerSource = new HierarchicalTreeDataGridSource<VisualizerNodeViewModel>(rootNodes)
            {
                Columns =
                {
                    new HierarchicalExpanderColumn<VisualizerNodeViewModel>(
                        new TextColumn<VisualizerNodeViewModel, string>("Visualizations", x => x.Name),
                        x => x is VisualizerFolderViewModel f ? f.Children : null,
                        x => x.IsDirectory)
                }
            };

            VisualizerSource.RowSelection!.SelectionChanged += VisualizerSelectionChanged;
            OnPropertyChanged(nameof(VisualizerSource));
        });
    }

    private void PopulateFolder(VisualizerFolderViewModel parent, string path)
    {
        foreach (var dir in Directory.GetDirectories(path))
        {
            var folderVm = new VisualizerFolderViewModel(Path.GetFileName(dir), dir);
            PopulateFolder(folderVm, dir);
            if (folderVm.Children.Count > 0)
                parent.Children.Add(folderVm);
        }

        foreach (var file in Directory.GetFiles(path, "*.milk"))
        {
            parent.Children.Add(new VisualizerFileViewModel(Path.GetFileNameWithoutExtension(file), file));
            lock(_allVisualizerPaths)
            {
                _allVisualizerPaths.Add(file);
            }
        }
    }

    partial void OnIsVisualizerRandomizerEnabledChanged(bool value)
    {
        if (value) _randomizerTimer.Start();
        else _randomizerTimer.Stop();
    }

    partial void OnVisualizerRandomizerIntervalSecondsChanged(int value)
    {
        _randomizerTimer.Interval = TimeSpan.FromSeconds(value);
    }

    private void RandomizerTimer_Tick(object? sender, EventArgs e)
    {
        lock(_allVisualizerPaths)
        {
            if (_allVisualizerPaths.Count > 0)
            {
                var random = new Random();
                int index = random.Next(_allVisualizerPaths.Count);
                SelectedVisualizerPath = _allVisualizerPaths[index];
            }
        }
    }

    private void VisualizerSelectionChanged(object? sender, TreeSelectionModelSelectionChangedEventArgs<VisualizerNodeViewModel> e)
    {
    }

    [RelayCommand]
    private void AddToFavorites(VisualizerFileViewModel? fileVm)
    {
        if (fileVm == null) return;
        var rootFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectM");
        var favFolder = Path.Combine(rootFolder, "Favorites");
        Directory.CreateDirectory(favFolder);

        var destPath = Path.Combine(favFolder, Path.GetFileName(fileVm.Path));
        if (fileVm.Path.Equals(destPath, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            File.Copy(fileVm.Path, destPath, true);
            
            // Also copy textures
            string sourceDir = Path.GetDirectoryName(fileVm.Path) ?? "";
            string content = File.ReadAllText(fileVm.Path);
            var regex = new System.Text.RegularExpressions.Regex(@"[a-zA-Z0-9_-]+\.(?:jpg|png|bmp|tga)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            
            foreach (System.Text.RegularExpressions.Match match in regex.Matches(content))
            {
                string textureName = match.Value;
                string sourceTex = Path.Combine(sourceDir, textureName);
                if (File.Exists(sourceTex))
                {
                    string destTex = Path.Combine(favFolder, textureName);
                    File.Copy(sourceTex, destTex, true);
                }
            }
            
            _ = LoadVisualizersAsync();
        }
        catch { }
    }

    [RelayCommand]
    private void RemoveFromFavorites(VisualizerFileViewModel? fileVm)
    {
        if (fileVm == null) return;
        try
        {
            File.Delete(fileVm.Path);
            _ = LoadVisualizersAsync();
        }
        catch { }
    }

    [RelayCommand]
    private async Task RenameVisualizerAsync(VisualizerFileViewModel? fileVm)
    {
        if (fileVm == null || string.IsNullOrEmpty(fileVm.Path)) return;

        var currentName = Path.GetFileNameWithoutExtension(fileVm.Path);
        var newName = await Jukebox.Views.RenameDialogView.ShowAsync(currentName);

        if (!string.IsNullOrWhiteSpace(newName) && newName != currentName)
        {
            try
            {
                var directory = Path.GetDirectoryName(fileVm.Path);
                if (directory == null) return;

                var newPath = Path.Combine(directory, newName + ".milk");
                if (!File.Exists(newPath))
                {
                    File.Move(fileVm.Path, newPath);
                    
                    if (SelectedVisualizerPath == fileVm.Path)
                    {
                        SelectedVisualizerPath = newPath;
                    }

                    await LoadVisualizersAsync();
                }
            }
            catch { }
        }
    }
}
