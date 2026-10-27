using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jukebox.Extensions;
using Jukebox.Models;
using Jukebox.Services;
using Jukebox.Plugin.Abstractions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Jukebox.ViewModels;

// Split across two partial files by concern:
//   JukeboxVisualizerViewModel.cs           - tree loading, selection, randomizer (this file)
//   JukeboxVisualizerViewModel.Favorites.cs - favorite add/remove/rename + tree sync
public partial class JukeboxVisualizerViewModel : ViewModelBase, IDisposable
{
    #region Fields & Constants
    private List<string> _allVisualizerPaths = new();
    private DispatcherTimer _randomizerTimer;
    private readonly Random _random = new();
    private Task? _loadVisualizersTask;
    private Task _presetSaveTask = Task.CompletedTask;
    private bool _isDisposed;
    private ObservableCollection<VisualizerNodeViewModel> _rootNodes = new();

    private readonly IPathProvider _pathProvider;
    private readonly IUserDialogService _dialogService;
    private readonly VisualizerPresetStore _presetStore;
    #endregion

    #region Observable Properties
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentVisualizerName))]
    [NotifyPropertyChangedFor(nameof(HasCurrentVisualizer))]
    private string? _selectedVisualizerPath;
    [ObservableProperty] private bool _isVisualizerRandomizerEnabled;
    [ObservableProperty] private int _visualizerRandomizerIntervalSeconds = 10;
    [ObservableProperty] private bool _hasPresets;
    [ObservableProperty] private string _presetsPathMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddToFavoritesCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveFromFavoritesCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameVisualizerCommand))]
    private VisualizerNodeViewModel? _selectedNode;
    #endregion

    #region Public Properties
    public ObservableCollection<VisualizerNodeViewModel> RootNodes => _rootNodes;
    public string? CurrentVisualizerName => Path.GetFileNameWithoutExtension(SelectedVisualizerPath);
    public bool HasCurrentVisualizer => !string.IsNullOrEmpty(SelectedVisualizerPath);

    public IReadOnlyList<int>? GetCurrentVisualizerIndexPath()
    {
        return Find(_rootNodes, new List<int>());

        IReadOnlyList<int>? Find(IList<VisualizerNodeViewModel> nodes, List<int> ancestors)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                var indexes = new List<int>(ancestors) { i };
                if (nodes[i] is VisualizerFileViewModel file &&
                    string.Equals(file.Path, SelectedVisualizerPath, StringComparison.OrdinalIgnoreCase))
                    return indexes;
                if (nodes[i] is VisualizerFolderViewModel folder && Find(folder.Children, indexes) is { } found)
                    return found;
            }
            return null;
        }
    }

    private JukeboxViewModel? _host;
    public void SetHost(JukeboxViewModel host)
    {
        _host = host;
    }

    public IReadOnlyList<IJukeboxVisualizerPlugin> AvailableVisualizerPlugins => _host?.VisualizerPlugins ?? Array.Empty<IJukeboxVisualizerPlugin>();

    public bool ShowEngineSelector => AvailableVisualizerPlugins.Count > 1;

    public IJukeboxVisualizerPlugin? ActiveVisualizer
    {
        get => _host?.ActiveVisualizer;
        set
        {
            if (_host != null && _host.ActiveVisualizer != value)
            {
                _host.ActiveVisualizer = value;
                SelectedVisualizerPath = null;
                OnPropertyChanged(nameof(ActiveVisualizer));
                // Reload visualizer presets/tree!
                LoadVisualizersAsync().SafeFireAndForget(nameof(LoadVisualizersAsync));
            }
        }
    }
    #endregion

    #region Constructor
    public JukeboxVisualizerViewModel() : this(PathProvider.Current, null) { }

    // Constructor added for testability — tests can inject stubs.
    public JukeboxVisualizerViewModel(IPathProvider pathProvider, IUserDialogService? dialogService = null)
    {
        _pathProvider = pathProvider ?? throw new ArgumentNullException(nameof(pathProvider));
        _dialogService = dialogService ?? new UserDialogService();
        _presetStore = new VisualizerPresetStore(_pathProvider);

        _randomizerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(VisualizerRandomizerIntervalSeconds) };
        _randomizerTimer.Tick += RandomizerTimer_Tick;

        // Initialization that touches the disk has been moved to InitializeAsync()
        // which should be called in the View's Loaded handler.
    }

    /// <summary>
    /// Loads the preset tree and restores the last-used original preset path.
    /// Should be called from the View's Loaded handler (or tests' setup phase).
    /// </summary>
    public Task InitializeAsync() => LoadVisualizersAsync();
    #endregion

    #region Public Methods
    private class TempFolderNode
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public List<TempFolderNode> SubFolders { get; } = new();
        public List<(string Name, string Path)> Files { get; } = new();
    }

    public Task LoadVisualizersAsync()
    {
        if (_isDisposed) return Task.CompletedTask;
        return _loadVisualizersTask is { IsCompleted: false }
            ? _loadVisualizersTask
            : _loadVisualizersTask = LoadVisualizersLoopAsync();
    }

    private async Task LoadVisualizersLoopAsync()
    {
        IJukeboxVisualizerPlugin? active;
        do
        {
            active = ActiveVisualizer;
            await LoadVisualizersCoreAsync(active);
        }
        while (!_isDisposed && !ReferenceEquals(active, ActiveVisualizer));
    }

    private async Task LoadVisualizersCoreAsync(IJukeboxVisualizerPlugin? active)
    {
        string? selectionAtStart = SelectedVisualizerPath;

        try
        {
            if (active == null)
            {
                await ClearVisualizersAsync(active, "No active visualizer plugin.");
                return;
            }

            if (!active.SupportsPresets)
            {
                await ClearVisualizersAsync(active, "Active visualizer does not support preset files.");
                return;
            }

            var rootFolder = active.PresetsDirectory;
            if (string.IsNullOrEmpty(rootFolder) || !await Task.Run(() => Directory.Exists(rootFolder)))
            {
                await ClearVisualizersAsync(active, $"No presets folder found at:\n{rootFolder}");
                return;
            }

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

                // Scan files directly in the presets folder root too
                var rootNodeDirect = new TempFolderNode { Name = "Presets", Path = rootFolder };
                foreach (var file in Directory.GetFiles(rootFolder, "*.milk"))
                {
                    rootNodeDirect.Files.Add((Path.GetFileNameWithoutExtension(file), file));
                    allPaths.Add(file);
                }
                if (rootNodeDirect.Files.Count > 0)
                {
                    scannedRoots.Add(rootNodeDirect);
                }
            });

            // Special Favorites node from ActiveVisualizer.FavoritesDirectory if it exists
            var favFolder = active.FavoritesDirectory;
            if (!string.IsNullOrEmpty(favFolder) && Directory.Exists(favFolder))
            {
                var favNode = new TempFolderNode { Name = "Favorites", Path = favFolder };
                await Task.Run(() =>
                {
                    foreach (var file in Directory.GetFiles(favFolder, "*.milk"))
                    {
                        favNode.Files.Add((Path.GetFileNameWithoutExtension(file), file));
                        allPaths.Add(file);
                    }
                });
                if (favNode.Files.Count > 0)
                {
                    scannedRoots.Insert(0, favNode);
                }
            }

            string? savedPath = await _presetStore.RestoreAsync(active.Id, active.CurrentPresetDirectory, allPaths);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_isDisposed || !ReferenceEquals(active, ActiveVisualizer)) return;
                lock (_allVisualizerPaths)
                {
                    _allVisualizerPaths.Clear();
                    _allVisualizerPaths.AddRange(allPaths);
                }

                _rootNodes.Clear();
                foreach (var node in scannedRoots)
                {
                    // Avoid duplicating Favorites folder if it's already there
                    if (node.Name == "Favorites" && _rootNodes.Any(r => r.Name == "Favorites")) continue;
                    _rootNodes.Add(BuildViewModelTree(node));
                }

                lock (_allVisualizerPaths)
                {
                    if (SelectedVisualizerPath == selectionAtStart && string.IsNullOrEmpty(SelectedVisualizerPath))
                    {
                        SelectedVisualizerPath = savedPath ?? _allVisualizerPaths.FirstOrDefault();
                    }
                }

                HasPresets = _rootNodes.Count > 0;
                if (!HasPresets)
                {
                    PresetsPathMessage = $"No presets found inside the folder:\n{rootFolder}";
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Visualizer] LoadVisualizersAsync failed: {ex.Message}");
        }
    }
    #endregion

    #region Private Methods
    private async Task ClearVisualizersAsync(IJukeboxVisualizerPlugin? active, string message)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_isDisposed || !ReferenceEquals(active, ActiveVisualizer)) return;
            _rootNodes.Clear();
            lock (_allVisualizerPaths) { _allVisualizerPaths.Clear(); }
            SelectedVisualizerPath = null;
            HasPresets = false;
            PresetsPathMessage = message;
        });
    }

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

    // Skips asset and platform folders that should not appear in a plugin's
    // preset tree.
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
        if (!_isDisposed && !string.IsNullOrEmpty(value) && ActiveVisualizer is { } active)
        {
            _presetSaveTask = _presetStore.SaveAsync(active.Id, value);
            _presetSaveTask.SafeFireAndForget("Save current visualization");
        }
    }

    partial void OnIsVisualizerRandomizerEnabledChanged(bool value)
    {
        if (value) _randomizerTimer.Start();
        else _randomizerTimer.Stop();
    }

    /// <summary>
    /// Suspends the randomizer timer without changing the
    /// <see cref="IsVisualizerRandomizerEnabled"/> toggle state. Used by
    /// JukeboxViewModel when the visualizer picker panel opens — pauses
    /// preset advancement so the user can browse/add the current preset
    /// without it changing out from under them. Pair with
    /// <see cref="ResumeTimer"/> on panel close.
    /// </summary>
    public void SuspendTimer() => _randomizerTimer.Stop();

    /// <summary>
    /// Resumes the randomizer timer if (and only if) the user's toggle
    /// is still on. Pair with <see cref="SuspendTimer"/>.
    /// </summary>
    public void ResumeTimer()
    {
        if (IsVisualizerRandomizerEnabled) _randomizerTimer.Start();
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

    /// <summary>
    /// Picks a random visualizer preset and applies it immediately, ignoring
    /// the current one (so each click yields a different preset). Functions
    /// like a one-shot "surprise me" button - distinct from the auto-advancing
    /// randomizer toggle, which fires on a timer.
    /// </summary>
    /// <remarks>
    /// Always enabled (no CanExecute gate). If no presets are loaded yet,
    /// or only one preset exists and it is already selected, the handler
    /// silently bails out - the click is a no-op rather than a disabled
    /// state, so the button is always visually clickable.
    /// </remarks>
    [RelayCommand]
    private void RandomPickPreset()
    {
        lock (_allVisualizerPaths)
        {
            int count = _allVisualizerPaths.Count;
            if (count == 0) return;

            // With only one preset there is nothing different to pick - bail.
            if (count == 1 && _allVisualizerPaths[0] == SelectedVisualizerPath) return;

            // Pick a random index that does NOT match the current preset.
            // With count == 1 and a different SelectedVisualizerPath (e.g.
            // current preset was deleted), just take index 0.
            string current = SelectedVisualizerPath ?? string.Empty;
            int index;
            int attempts = 0;
            do
            {
                index = _random.Next(count);
                attempts++;
                // Bail-out guard: in pathological cases (count == 1) the
                // loop would otherwise spin forever.
                if (attempts > 16) break;
            } while (_allVisualizerPaths[index] == current && count > 1);

            SelectedVisualizerPath = _allVisualizerPaths[index];
        }
    }
    #endregion

    #region Dispose
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _randomizerTimer.Stop();
        _randomizerTimer.Tick -= RandomizerTimer_Tick;
    }

    public async Task FlushPresetAsync()
    {
        _randomizerTimer.Stop();
        if (_loadVisualizersTask is { } pending) await pending;
        await _presetSaveTask;
    }
    #endregion
}
