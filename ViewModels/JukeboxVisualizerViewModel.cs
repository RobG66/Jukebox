using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jukebox.Extensions;
using Jukebox.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

    // REFACTOR: Regex cached as static readonly field instead of being
    // recompiled on every AddToFavorites call (was smell §4.5 Warning:
    // Regex compilation on every AddToFavorites call).
    private static readonly Regex TextureFileRegex = new(
        @"[a-zA-Z0-9_-]+\.(?:jpg|png|bmp|tga)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IPathProvider _pathProvider;
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
    public JukeboxVisualizerViewModel() : this(PathProvider.Current)
    {
    }

    // Constructor added for testability — tests can inject a stub IPathProvider.
    public JukeboxVisualizerViewModel(IPathProvider pathProvider)
    {
        _pathProvider = pathProvider ?? throw new ArgumentNullException(nameof(pathProvider));

        _randomizerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(VisualizerRandomizerIntervalSeconds) };
        _randomizerTimer.Tick += RandomizerTimer_Tick;

        VisualizerSource = new HierarchicalTreeDataGridSource<VisualizerNodeViewModel>(_rootNodes)
        {
            Columns =
            {
                new HierarchicalExpanderColumn<VisualizerNodeViewModel>(
                    new TextColumn<VisualizerNodeViewModel, string>(
                        "Visualizations",
                        x => x.Name,
                        new GridLength(1, GridUnitType.Star),
                        new TextColumnOptions<VisualizerNodeViewModel>
                        {
                            TextTrimming = TextTrimming.CharacterEllipsis
                        }),
                    x => x is VisualizerFolderViewModel f ? f.Children : null,
                    x => x.IsDirectory)
            }
        };

        VisualizerSource.RowSelection!.SelectionChanged += (s, e) =>
        {
            SelectedNode = VisualizerSource.RowSelection?.SelectedItems?.FirstOrDefault();
        };

        // REFACTOR: synchronous file IO in constructor moved to async
        // InitializeAsync (was smell §4.5 Critical: Synchronous file IO on UI
        // thread in constructor). The constructor no longer touches the disk.
        // The View should call InitializeAsync() in its Loaded handler.
    }

    /// <summary>
    /// Loads the last-selected visualizer from disk asynchronously. Should be
    /// called from the View's Loaded handler (or tests' setup phase).
    /// </summary>
    public async Task InitializeAsync()
    {
        var settingsFile = _pathProvider.LastPresetFile;
        var savedPath = await Task.Run(async () =>
        {
            if (!File.Exists(settingsFile)) return null;
            try { return (await File.ReadAllTextAsync(settingsFile)).Trim(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Visualizer] Failed reading last_preset.txt: {ex.Message}"); return null; }
        });

        if (!string.IsNullOrEmpty(savedPath) && await Task.Run(() => File.Exists(savedPath)))
        {
            SelectedVisualizerPath = savedPath;
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
        // REFACTOR: Interlocked.CompareExchange would be slightly cleaner,
        // but the existing bool flag is sufficient for the single-threaded
        // UI context. Left as-is to minimize diff (smell §4.5 Minor).
        if (_isLoadingVisualizers) return;
        _isLoadingVisualizers = true;

        try
        {
            var rootFolder = _pathProvider.ProjectMPresetsDirectory;
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
            // REFACTOR: fire-and-forget Task.Run → SafeFireAndForget (was smell
            // §4.5 Critical: File.WriteAllText in PropertyChanged handler).
            // Exceptions during the write are now captured and logged instead
            // of being silently swallowed.
            Task.Run(() =>
            {
                try
                {
                    File.WriteAllText(_pathProvider.LastPresetFile, value);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Visualizer] Failed to persist last preset: {ex.Message}");
                }
            }).SafeFireAndForget(nameof(OnSelectedVisualizerPathChanged));
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
    private async Task AddToFavorites()
    {
        if (SelectedNode is not VisualizerFileViewModel fileVm) return;
        var favFolder = _pathProvider.ProjectMFavoritesDirectory;
        var destPath = Path.Combine(favFolder, Path.GetFileName(fileVm.Path));
        if (fileVm.Path.Equals(destPath, StringComparison.OrdinalIgnoreCase)) return;

        // REFACTOR: confirm before overwrite (was smell §4.5 Warning: File.Copy
        // without overwrite check semantics). The user may have customized the
        // existing favorite — silent overwrite would lose their work.
        if (File.Exists(destPath))
        {
            bool overwrite = await Jukebox.Views.ThreeButtonDialogView.ShowConfirmAsync(
                "File Already in Favorites",
                $"A file named '{Path.GetFileName(fileVm.Path)}' already exists in Favorites.\n" +
                "Overwrite it? This will lose any customizations you made to the favorite.",
                confirmText: "Overwrite",
                cancelText: "Cancel");
            if (!overwrite) return;
        }

        try
        {
            if (!Directory.Exists(favFolder))
            {
                Directory.CreateDirectory(favFolder);
            }

            File.Copy(fileVm.Path, destPath, true);

            // Also copy textures — uses the cached static Regex instead of
            // recompiling on every call.
            string sourceDir = Path.GetDirectoryName(fileVm.Path) ?? "";
            string content = await File.ReadAllTextAsync(fileVm.Path);

            foreach (Match match in TextureFileRegex.Matches(content))
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
            // REFACTOR: Console.WriteLine → Debug.WriteLine (smell §4.5, §6.5).
            System.Diagnostics.Debug.WriteLine($"[Error] AddToFavorites failed: {ex.Message}");
            await Jukebox.Views.ThreeButtonDialogView.ShowErrorAsync(
                "Add to Favorites Failed",
                ex.Message);
        }
    }
    private bool CanAddToFavorites() => SelectedNode is VisualizerFileViewModel { IsFavorite: false };

    private void UpdateTreeForAddedFavorite(string destPath)
    {
        var favFolder = _rootNodes.OfType<VisualizerFolderViewModel>().FirstOrDefault(x => x.Name == "Favorites");
        if (favFolder == null)
        {
            var favPath = _pathProvider.ProjectMFavoritesDirectory;
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
    private async Task RemoveFromFavorites()
    {
        if (SelectedNode is not VisualizerFileViewModel fileVm) return;

        var favFolder = _pathProvider.ProjectMFavoritesDirectory;

        if (!fileVm.Path.StartsWith(favFolder, StringComparison.OrdinalIgnoreCase)) return;

        // REFACTOR: confirm before delete (was smell §4.5 Warning: File.Delete
        // with no confirmation). The delete is irreversible — there's no
        // recycle bin on Linux, and even on Windows File.Delete bypasses it.
        bool confirm = await Jukebox.Views.ThreeButtonDialogView.ShowConfirmAsync(
            "Delete Favorite",
            $"Permanently delete '{Path.GetFileName(fileVm.Path)}' from Favorites?\n" +
            "This cannot be undone.",
            confirmText: "Delete",
            cancelText: "Cancel",
            icon: Jukebox.Views.DialogIconTheme.Warning);
        if (!confirm) return;

        try
        {
            File.Delete(fileVm.Path);
            UpdateTreeForRemovedFavorite(fileVm.Path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Error] RemoveFromFavorites failed: {ex.Message}");
            await Jukebox.Views.ThreeButtonDialogView.ShowErrorAsync(
                "Remove Favorite Failed",
                ex.Message);
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
                // REFACTOR: Console.WriteLine → Debug.WriteLine + user-facing
                // error dialog (was smell §4.5 Warning: Fire-and-forget
                // RenameVisualizerAsync).
                System.Diagnostics.Debug.WriteLine($"[Error] RenameVisualizerAsync failed: {ex.Message}");
                await Jukebox.Views.ThreeButtonDialogView.ShowErrorAsync(
                    "Rename Failed",
                    $"Could not rename '{currentName}' to '{newName}'.\n\n{ex.Message}");
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
