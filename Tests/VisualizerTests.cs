using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Jukebox.Plugin.Abstractions;
using Jukebox.Services;
using Jukebox.ViewModels;
using Jukebox.Views;
using Xunit;

namespace Jukebox.Tests;

public sealed class VisualizerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jukebox-visualizer-tests-" + Guid.NewGuid().ToString("N"));

    public VisualizerTests()
    {
        Directory.CreateDirectory(_directory);
        PathProvider.Override(new TestPaths(_directory));
    }

    [AvaloniaFact]
    public async Task RestartRestoresOriginalPresetAndLinkRevealsItWithoutChangingPlayback()
    {
        var plugin = new TestVisualizer(_directory);
        CreatePreset("Pack/first.milk", "first");
        for (int i = 0; i < 80; i++) CreatePreset($"Pack/Folder{i}/other.milk", $"other {i}");
        string playing = CreatePreset("Pack/Nested/playing.milk", "playing");
        await using (var host = CreateHost(plugin))
        {
            await host.VisualizerViewModel.InitializeAsync();
            host.VisualizerViewModel.SelectedVisualizerPath = playing;
        }

        await using var restarted = CreateHost(plugin);
        await restarted.VisualizerViewModel.InitializeAsync();
        Assert.Equal(playing, restarted.VisualizerViewModel.SelectedVisualizerPath);
        Assert.Equal("playing", restarted.VisualizerViewModel.CurrentVisualizerName);
        Assert.False(Directory.Exists(plugin.CurrentPresetDirectory));

        var view = new VisualizerPickerView { DataContext = restarted };
        var window = new Window { Width = 400, Height = 480, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            var grid = view.FindControl<TreeDataGrid>("VisualizerTreeDataGrid")!;
            var source = (HierarchicalTreeDataGridSource<VisualizerNodeViewModel>)grid.Source!;
            Assert.Single(source.Rows);
            view.FindControl<Button>("CurrentVisualizerLink")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var selected = Assert.IsType<VisualizerFileViewModel>(source.RowSelection!.SelectedItem);
            Assert.Equal(playing, selected.Path);
            Assert.Equal(playing, restarted.VisualizerViewModel.SelectedVisualizerPath);
            int row = source.Rows.ModelIndexToRowIndex(source.RowSelection.SelectedIndex);
            Assert.True(row >= 0);
            var rowControl = grid.TryGetRow(row);
            Assert.NotNull(rowControl);
            double rowTop = rowControl.TranslatePoint(default, grid)!.Value.Y;
            Assert.InRange(rowTop, 0, grid.Bounds.Height - rowControl.Bounds.Height);

            // Updates from random picks also update the displayed current preset.
            restarted.VisualizerViewModel.RandomPickPresetCommand.Execute(null);
            Assert.NotEqual(playing, restarted.VisualizerViewModel.SelectedVisualizerPath);
            Assert.Equal(Path.GetFileNameWithoutExtension(restarted.VisualizerViewModel.SelectedVisualizerPath),
                restarted.VisualizerViewModel.CurrentVisualizerName);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task BothFavoritesCommandsCopyOriginalPresetsAndTexturesAndFavoritesRestore()
    {
        var plugin = new TestVisualizer(_directory);
        string playing = CreatePreset("Pack/playing.milk", "texture=custom.jpg");
        string selected = CreatePreset("Pack/selected.milk", "texture=custom.jpg\nselected");
        string texture = Path.Combine(Path.GetDirectoryName(playing)!, "custom.jpg");
        await File.WriteAllBytesAsync(texture, new byte[] { 1, 2, 3 });
        string favorite = Path.Combine(plugin.FavoritesDirectory!, "playing.milk");

        await using (var host = CreateHost(plugin))
        {
            var model = host.VisualizerViewModel;
            await model.InitializeAsync();
            model.SelectedVisualizerPath = playing;
            model.SelectedNode = new VisualizerFileViewModel("selected", selected);
            await model.AddCurrentToFavoritesCommand.ExecuteAsync(null);
            await model.AddToFavoritesCommand.ExecuteAsync(null);
            Assert.Equal(await File.ReadAllTextAsync(playing), await File.ReadAllTextAsync(favorite));
            Assert.Equal(await File.ReadAllTextAsync(selected), await File.ReadAllTextAsync(Path.Combine(plugin.FavoritesDirectory!, "selected.milk")));
            Assert.Equal(await File.ReadAllBytesAsync(texture), await File.ReadAllBytesAsync(Path.Combine(plugin.FavoritesDirectory!, "custom.jpg")));
            Assert.Equal(playing, model.SelectedVisualizerPath);

            // Repeated clicks must preserve an existing customized favorite.
            await File.WriteAllTextAsync(favorite, "customized favorite");
            await model.AddCurrentToFavoritesCommand.ExecuteAsync(null);
            Assert.Equal("customized favorite", await File.ReadAllTextAsync(favorite));
            model.SelectedVisualizerPath = favorite;
            await model.AddCurrentToFavoritesCommand.ExecuteAsync(null);
            Assert.Equal("customized favorite", await File.ReadAllTextAsync(favorite));
            Assert.NotNull(model.GetCurrentVisualizerIndexPath());
        }

        await using var restarted = CreateHost(plugin);
        await restarted.VisualizerViewModel.InitializeAsync();
        Assert.Equal(favorite, restarted.VisualizerViewModel.SelectedVisualizerPath);
        Assert.NotNull(restarted.VisualizerViewModel.GetCurrentVisualizerIndexPath());
        Assert.False(Directory.Exists(plugin.CurrentPresetDirectory));
    }

    [AvaloniaFact]
    public async Task LegacyCopyMigratesToMatchingOriginalAndDeletedSavedPresetFallsBack()
    {
        var plugin = new TestVisualizer(_directory);
        string other = CreatePreset("Other/same.milk", "different content");
        string original = CreatePreset("Pack/same.milk", "original content");
        Directory.CreateDirectory(plugin.CurrentPresetDirectory!);
        string copy = Path.Combine(plugin.CurrentPresetDirectory!, "same.milk");
        await File.WriteAllTextAsync(copy, "original content");
        await using (var host = CreateHost(plugin))
        {
            await host.VisualizerViewModel.InitializeAsync();
            Assert.Equal(original, host.VisualizerViewModel.SelectedVisualizerPath);
        }
        File.Delete(original);
        await using var restarted = CreateHost(plugin);
        await restarted.VisualizerViewModel.InitializeAsync();
        Assert.Equal(other, restarted.VisualizerViewModel.SelectedVisualizerPath);
        Assert.Equal("original content", await File.ReadAllTextAsync(copy));
    }

    [Fact]
    public async Task RapidSavesKeepLatestPathAndUsePortableRelativePaths()
    {
        var paths = new TestPaths(_directory);
        var store = new VisualizerPresetStore(paths);
        string first = CreatePreset("Pack/first.milk", "first");
        string last = CreatePreset("Pack/last.milk", "last");
        var saves = Enumerable.Range(0, 20).Select(i => store.SaveAsync("projectm", i == 19 ? last : first)).ToArray();
        await Task.WhenAll(saves);
        var restored = new VisualizerPresetStore(paths);
        Assert.Equal(last, await restored.RestoreAsync("projectm", null, new[] { first, last }));
        string json = await File.ReadAllTextAsync(Path.Combine(paths.SettingsDirectory, "visualizer-presets.json"), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(_directory, json);
    }

    private string CreatePreset(string relativePath, string content)
    {
        string path = Path.Combine(_directory, "presets", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static JukeboxViewModel CreateHost(IJukeboxVisualizerPlugin plugin) => new()
    {
        VisualizerPlugins = new[] { plugin },
        ActiveVisualizer = plugin
    };

    public void Dispose()
    {
        PathProvider.Reset();
        Directory.Delete(_directory, recursive: true);
    }

    private sealed class TestPaths(string directory) : IPathProvider
    {
        public string NativeLibDirectory => Path.Combine(directory, "lib");
        public string SettingsDirectory => Path.Combine(directory, "Settings");
        public string EqSettingsFile => Path.Combine(SettingsDirectory, "eq.json");
        public string PlaylistsDirectory => Path.Combine(directory, "Playlists");
        public string CacheDirectory => Path.Combine(directory, "Cache");
    }

    private sealed class TestVisualizer(string directory) : IJukeboxVisualizerPlugin
    {
        public string Id => "projectm";
        public string DisplayName => "Test visualizer";
        public bool IsAvailable => true;
        public bool SupportsPresets => true;
        public string? PresetsDirectory => Path.Combine(directory, "presets");
        public string? FavoritesDirectory => Path.Combine(PresetsDirectory!, "Favorites");
        public string? CurrentPresetDirectory => Path.Combine(directory, "current_preset");
        public Task InitializeAsync() => Task.CompletedTask;
        public Control? CreateControl() => null;
        public void StartEngine(Control control) { }
        public void FeedPcm(Control control, short[] pcm) { }
        public void TryDispose(Control? control) { }
        public void LoadPreset(Control control, string presetPath) { }
    }
}
