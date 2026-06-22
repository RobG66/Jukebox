using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;

namespace Jukebox.ViewModels;

public partial class JukeboxVisualizerViewModel : ViewModelBase, IDisposable
{
    #region Fields & Constants
    private List<string> _allVisualizerPaths = new();
    private DispatcherTimer _randomizerTimer;
    private readonly Random _random = new();
    private bool _isLoadingVisualizers = false;
    #endregion

    #region Observable Properties
    [ObservableProperty] private string? _selectedVisualizerPath;
    [ObservableProperty] private bool _isVisualizerRandomizerEnabled;
    [ObservableProperty] private int _visualizerRandomizerIntervalSeconds = 10;
    #endregion

    #region Public Properties
    public HierarchicalTreeDataGridSource<VisualizerNodeViewModel>? VisualizerSource { get; private set; }
    #endregion

    #region Constructor
    public JukeboxVisualizerViewModel()
    {
        _randomizerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(VisualizerRandomizerIntervalSeconds) };
        _randomizerTimer.Tick += RandomizerTimer_Tick;

        // Restore last visualizer from settings file
        var settingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectM", "last_preset.txt");
        if (File.Exists(settingsFile))
        {
            var savedPath = File.ReadAllText(settingsFile).Trim();
            if (File.Exists(savedPath))
            {
                SelectedVisualizerPath = savedPath;
            }
        }
    }
    #endregion

    #region Public Methods
    public async Task LoadVisualizersAsync()
    {
        if (_isLoadingVisualizers) return;
        _isLoadingVisualizers = true;

        try
        {
            var rootFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectM", "Presets");
        if (!Directory.Exists(rootFolder)) return;

        var rootNodes = new ObservableCollection<VisualizerNodeViewModel>();

        await Task.Run(() =>
        {
            lock (_allVisualizerPaths)
            {
                _allVisualizerPaths.Clear();
            }
            var directories = Directory.GetDirectories(rootFolder);
            foreach (var dir in directories)
            {
                var folderName = Path.GetFileName(dir);
                if (folderName == "win-x64" || folderName == "textures") continue;

                var folderVm = new VisualizerFolderViewModel(folderName, dir);
                PopulateFolder(folderVm, dir);
                if (folderVm.Children.Count > 0)
                {
                    rootNodes.Add(folderVm);
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

            OnPropertyChanged(nameof(VisualizerSource));

            lock (_allVisualizerPaths)
            {
                if (string.IsNullOrEmpty(SelectedVisualizerPath) && _allVisualizerPaths.Count > 0)
                {
                    SelectedVisualizerPath = _allVisualizerPaths[0];
                }
            }
        });
        }
        finally
        {
            _isLoadingVisualizers = false;
        }
    }
    #endregion

    #region Private Methods
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
            lock (_allVisualizerPaths)
            {
                _allVisualizerPaths.Add(file);
            }
        }
    }
    #endregion

    #region Property Change Callbacks
    partial void OnSelectedVisualizerPathChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            try
            {
                var settingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectM", "last_preset.txt");
                File.WriteAllText(settingsFile, value);
            }
            catch { }
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
    #endregion

    #region Commands
    private void RandomizerTimer_Tick(object? sender, EventArgs e)
    {
        lock (_allVisualizerPaths)
        {
            if (_allVisualizerPaths.Count > 0)
            {
                int index = _random.Next(_allVisualizerPaths.Count);
                SelectedVisualizerPath = _allVisualizerPaths[index];
            }
        }
    }

    [RelayCommand]
    private void AddToFavorites(VisualizerFileViewModel? fileVm)
    {
        if (fileVm == null) return;
        var rootFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectM", "Presets");
        var favFolder = Path.Combine(rootFolder, "Favorites");
        var destPath = Path.Combine(favFolder, Path.GetFileName(fileVm.Path));
        if (fileVm.Path.Equals(destPath, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            if (!Directory.Exists(favFolder))
            {
                Directory.CreateDirectory(favFolder);
            }

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
        catch (Exception ex) 
        { 
            Console.WriteLine($"[Error] AddToFavorites failed: {ex.Message}");
        }
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
        catch (Exception ex) 
        { 
            Console.WriteLine($"[Error] RemoveFromFavorites failed: {ex.Message}");
        }
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
            catch (Exception ex) 
            { 
                Console.WriteLine($"[Error] RenameVisualizerAsync failed: {ex.Message}");
            }
        }
    }
    #endregion

    #region Dispose
    public void Dispose()
    {
        _randomizerTimer?.Stop();
    }
    #endregion
}
