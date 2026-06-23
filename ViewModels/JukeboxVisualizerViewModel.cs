using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Jukebox.ViewModels;

public partial class JukeboxVisualizerViewModel : ViewModelBase, IDisposable
{
    #region Fields & Constants
    private List<string> _allVisualizerPaths = new();
    private DispatcherTimer _randomizerTimer;
    private readonly Random _random = new();
    private bool _isLoadingVisualizers = false;
    private ObservableCollection<VisualizerNodeViewModel> _rootNodes = new();
    #endregion

    #region Observable Properties
    [ObservableProperty] private string? _selectedVisualizerPath;
    [ObservableProperty] private bool _isVisualizerRandomizerEnabled;
    [ObservableProperty] private int _visualizerRandomizerIntervalSeconds = 10;

    private VisualizerNodeViewModel? _selectedNode;
    public VisualizerNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (SetProperty(ref _selectedNode, value))
            {
                AddToFavoritesCommand.NotifyCanExecuteChanged();
                RemoveFromFavoritesCommand.NotifyCanExecuteChanged();
                RenameVisualizerCommand.NotifyCanExecuteChanged();
            }
        }
    }
    #endregion

    #region Public Properties
    public HierarchicalTreeDataGridSource<VisualizerNodeViewModel>? VisualizerSource { get; private set; }
    #endregion

    #region Constructor
    public JukeboxVisualizerViewModel()
    {
        _randomizerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(VisualizerRandomizerIntervalSeconds) };
        _randomizerTimer.Tick += RandomizerTimer_Tick;

        VisualizerSource = new HierarchicalTreeDataGridSource<VisualizerNodeViewModel>(_rootNodes)
        {
            Columns =
            {
                new HierarchicalExpanderColumn<VisualizerNodeViewModel>(
                    new TextColumn<VisualizerNodeViewModel, string>("Visualizations", x => x.Name),
                    x => x is VisualizerFolderViewModel f ? f.Children : null,
                    x => x.IsDirectory)
            }
        };

        VisualizerSource.RowSelection!.SelectionChanged += (s, e) =>
        {
            SelectedNode = VisualizerSource.RowSelection?.SelectedItems?.FirstOrDefault();
        };

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
    private class TempFolderNode
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public List<TempFolderNode> SubFolders { get; } = new();
        public List<(string Name, string Path)> Files { get; } = new();
    }

    public async Task LoadVisualizersAsync()
    {
        if (_isLoadingVisualizers) return;
        _isLoadingVisualizers = true;

        try
        {
            var rootFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectM", "Presets");
            if (!Directory.Exists(rootFolder)) return;

            var scannedRoots = new List<TempFolderNode>();
            var allPaths = new List<string>();

            await Task.Run(() =>
            {
                var directories = Directory.GetDirectories(rootFolder);
                foreach (var dir in directories)
                {
                    var folderName = Path.GetFileName(dir);
                    if (IsNativeOrSystemFolder(folderName)) continue;

                    var rootNode = ScanFolder(folderName, dir, allPaths);
                    if (rootNode.SubFolders.Count > 0 || rootNode.Files.Count > 0)
                    {
                        scannedRoots.Add(rootNode);
                    }
                }
            });

            Dispatcher.UIThread.Post(() =>
            {
                lock (_allVisualizerPaths)
                {
                    _allVisualizerPaths.Clear();
                    _allVisualizerPaths.AddRange(allPaths);
                }

                _rootNodes.Clear();
                foreach (var node in scannedRoots)
                {
                    _rootNodes.Add(BuildViewModelTree(node));
                }

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
    private TempFolderNode ScanFolder(string name, string path, List<string> allPaths)
    {
        var node = new TempFolderNode { Name = name, Path = path };

        foreach (var dir in Directory.GetDirectories(path))
        {
            var folderName = Path.GetFileName(dir);
            if (IsNativeOrSystemFolder(folderName)) continue;

            var subNode = ScanFolder(folderName, dir, allPaths);
            if (subNode.SubFolders.Count > 0 || subNode.Files.Count > 0)
            {
                node.SubFolders.Add(subNode);
            }
        }

        foreach (var file in Directory.GetFiles(path, "*.milk"))
        {
            node.Files.Add((Path.GetFileNameWithoutExtension(file), file));
            allPaths.Add(file);
        }

        return node;
    }

    // Skips native runtime directories (win-x64, linux-x64, osx-arm64, etc.) and asset folders
    private static bool IsNativeOrSystemFolder(string folderName) =>
        string.Equals(folderName, "textures", StringComparison.OrdinalIgnoreCase) ||
        folderName.StartsWith("win-",   StringComparison.OrdinalIgnoreCase) ||
        folderName.StartsWith("linux-", StringComparison.OrdinalIgnoreCase) ||
        folderName.StartsWith("osx-",   StringComparison.OrdinalIgnoreCase);

    private VisualizerFolderViewModel BuildViewModelTree(TempFolderNode node)
    {
        var folderVm = new VisualizerFolderViewModel(node.Name, node.Path);
        foreach (var subFolder in node.SubFolders)
        {
            folderVm.Children.Add(BuildViewModelTree(subFolder));
        }
        foreach (var file in node.Files)
        {
            folderVm.Children.Add(new VisualizerFileViewModel(file.Name, file.Path));
        }
        return folderVm;
    }
    #endregion

    #region Property Change Callbacks
    partial void OnSelectedVisualizerPathChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var settingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectM", "last_preset.txt");
                    File.WriteAllText(settingsFile, value);
                }
                catch { }
            });
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

    [RelayCommand(CanExecute = nameof(CanAddToFavorites))]
    private void AddToFavorites()
    {
        if (SelectedNode is not VisualizerFileViewModel fileVm) return;
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

            UpdateTreeForAddedFavorite(destPath);
        }
        catch (Exception ex) 
        { 
            Console.WriteLine($"[Error] AddToFavorites failed: {ex.Message}");
        }
    }
    private bool CanAddToFavorites() => SelectedNode is VisualizerFileViewModel { IsFavorite: false };

    private void UpdateTreeForAddedFavorite(string destPath)
    {
        var favFolder = _rootNodes.OfType<VisualizerFolderViewModel>().FirstOrDefault(x => x.Name == "Favorites");
        if (favFolder == null)
        {
            var favPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectM", "Presets", "Favorites");
            favFolder = new VisualizerFolderViewModel("Favorites", favPath);
            _rootNodes.Insert(0, favFolder);
        }
        
        var fileName = Path.GetFileNameWithoutExtension(destPath);
        if (!favFolder.Children.Any(x => x is VisualizerFileViewModel f && f.Path == destPath))
        {
            favFolder.Children.Add(new VisualizerFileViewModel(fileName, destPath));
        }

        lock (_allVisualizerPaths)
        {
            if (!_allVisualizerPaths.Contains(destPath))
                _allVisualizerPaths.Add(destPath);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveFromFavorites))]
    private void RemoveFromFavorites()
    {
        if (SelectedNode is not VisualizerFileViewModel fileVm) return;
        
        var rootFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectM", "Presets");
        var favFolder = Path.Combine(rootFolder, "Favorites");
        
        if (!fileVm.Path.StartsWith(favFolder, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            File.Delete(fileVm.Path);
            UpdateTreeForRemovedFavorite(fileVm.Path);
        }
        catch (Exception ex) 
        { 
            Console.WriteLine($"[Error] RemoveFromFavorites failed: {ex.Message}");
        }
    }
    private bool CanRemoveFromFavorites() => SelectedNode is VisualizerFileViewModel { IsFavorite: true };

    private void UpdateTreeForRemovedFavorite(string removedPath)
    {
        var favFolder = _rootNodes.OfType<VisualizerFolderViewModel>().FirstOrDefault(x => x.Name == "Favorites");
        if (favFolder != null)
        {
            var fileNode = favFolder.Children.OfType<VisualizerFileViewModel>().FirstOrDefault(x => x.Path == removedPath);
            if (fileNode != null)
            {
                favFolder.Children.Remove(fileNode);
            }
            if (favFolder.Children.Count == 0)
            {
                _rootNodes.Remove(favFolder);
            }
        }

        lock (_allVisualizerPaths)
        {
            _allVisualizerPaths.Remove(removedPath);
            if (SelectedVisualizerPath == removedPath)
            {
                SelectedVisualizerPath = _allVisualizerPaths.FirstOrDefault();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanRenameVisualizer))]
    private async Task RenameVisualizerAsync()
    {
        if (SelectedNode is not VisualizerFileViewModel fileVm || string.IsNullOrEmpty(fileVm.Path)) return;

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

                    lock (_allVisualizerPaths)
                    {
                        var index = _allVisualizerPaths.IndexOf(fileVm.Path);
                        if (index >= 0)
                        {
                            _allVisualizerPaths[index] = newPath;
                        }
                    }

                    if (SelectedVisualizerPath == fileVm.Path)
                    {
                        SelectedVisualizerPath = newPath;
                    }

                    fileVm.Name = newName;
                    fileVm.Path = newPath;
                    
                    AddToFavoritesCommand.NotifyCanExecuteChanged();
                    RemoveFromFavoritesCommand.NotifyCanExecuteChanged();
                    RenameVisualizerCommand.NotifyCanExecuteChanged();
                }
            }
            catch (Exception ex) 
            { 
                Console.WriteLine($"[Error] RenameVisualizerAsync failed: {ex.Message}");
            }
        }
    }
    private bool CanRenameVisualizer() => SelectedNode is VisualizerFileViewModel;
    #endregion

    #region Dispose
    public void Dispose()
    {
        _randomizerTimer?.Stop();
    }
    #endregion
}
