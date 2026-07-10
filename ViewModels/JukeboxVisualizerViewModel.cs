using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jukebox.Extensions;
using Jukebox.Models;
using Jukebox.Services;
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
    private bool _isLoadingVisualizers = false;
    private ObservableCollection<VisualizerNodeViewModel> _rootNodes = new();

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

        // Initialization that touches the disk has been moved to InitializeAsync()
        // which should be called in the View's Loaded handler.
    }

    /// <summary>
    /// Restores the last-used visualizer preset from the current_preset directory.
    /// Should be called from the View's Loaded handler (or tests' setup phase).
    /// </summary>
    public async Task InitializeAsync()
    {
        var currentPresetDir = _pathProvider.CurrentPresetDirectory;
        var savedPath = await Task.Run(() =>
        {
            if (!Directory.Exists(currentPresetDir)) return null;
            try { return Directory.GetFiles(currentPresetDir, "*.milk").FirstOrDefault(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Visualizer] Failed scanning current_preset directory: {ex.Message}"); return null; }
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
        if (_isLoadingVisualizers) return;
        _isLoadingVisualizers = true;

        try
        {
            var rootFolder = _pathProvider.ProjectMPresetsDirectory;
            // No-op cleanly when the ProjectM drop-in folder is absent.
            // This is the normal state when the user has not added the
            // optional ProjectM drop-in: the visualizer button is hidden
            // (IsVisualizerAvailable = false) and the picker tree is
            // empty. Audio playback is unaffected.
            if (!Directory.Exists(rootFolder))
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Visualizer] ProjectM presets directory not present — " +
                    "visualizer picker will be empty. Audio playback is unaffected.");
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

    // Skips asset folders that shouldn't appear in the preset tree.
    // (The native libprojectM binaries no longer live under ProjectM/ —
    // they're in the flat lib/ folder alongside bass.dll, libmpv-2.dll,
    // etc. The win-/linux-/osx- prefix checks are kept defensively in
    // case a user drops an old-style ProjectM folder in.)
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
        // No persistence needed here — ProjectMControl.LoadPreset copies the
        // selected .milk file (and its textures) into current_preset/, which
        // serves as the restore point on next launch.
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
        _randomizerTimer?.Stop();
    }
    #endregion
}
