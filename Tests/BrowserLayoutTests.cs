using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jukebox.Plugin.Abstractions;
using Jukebox.Plugin.InternetArchive;
using Jukebox.Plugin.InternetArchive.Views;
using Jukebox.Plugin.RadioBrowser;
using Jukebox.Plugin.RadioBrowser.Views;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Jukebox.Tests.HeadlessTestApplication))]

namespace Jukebox.Tests;

public static class HeadlessTestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class BrowserLayoutTests
{
    [AvaloniaTheory]
    [InlineData(620)]
    [InlineData(830)]
    [InlineData(1000)]
    public async Task RadioFooterAndFiltersFitTheirAllocatedWidth(double width)
    {
        var context = new TestBrowserContext();
        using var model = new RadioBrowserViewModel(context);
        await model.InitializeAsync();
        model.TotalPages = 358;
        model.StatusText = "Loaded 35,726 stations from cache; a deliberately long status message.";
        var view = new RadioBrowserView { DataContext = model };
        var window = new Window { Width = width, Height = 640, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var actions = view.FindControl<StackPanel>("FooterActions")!;
            var pagination = view.FindControl<StackPanel>("FooterPagination")!;
            var status = view.FindControl<TextBlock>("FooterStatus")!;
            AssertInside(view, actions, 8);
            AssertInside(view, pagination, 8);
            AssertInside(view, status, 8);
            var actionsPosition = actions.TranslatePoint(default, view)!.Value;
            var paginationPosition = pagination.TranslatePoint(default, view)!.Value;
            Assert.True(paginationPosition.X + pagination.Bounds.Width <= actionsPosition.X,
                "Pagination overlaps playback actions.");
            foreach (var control in view.GetVisualDescendants().OfType<Control>()
                .Where(control => control is Button or ComboBox)
                .Where(control => control.IsEffectivelyVisible))
                AssertInside(view, control, 0);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(570, BrowserContentLayout.Stacked)]
    [InlineData(800, BrowserContentLayout.SideBySide)]
    [InlineData(1200, BrowserContentLayout.SideBySide)]
    public void ArchiveActionsFitWithFormatsAndALongItemTitle(double width, BrowserContentLayout layout)
    {
        using var service = new TestArchiveService();
        using var model = new InternetArchiveBrowserViewModel(new TestBrowserContext(), service, layout);
        model.CurrentAlbum = new ArchiveAlbum { Title = new string('W', 120) };
        model.SelectedTrack = model.Tracks[0];
        var view = new InternetArchiveBrowserView { DataContext = model };
        var window = new Window { Width = width, Height = 760, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            foreach (var control in view.GetVisualDescendants().OfType<Control>()
                .Where(control => control is Button or ComboBox)
                .Where(control => control.IsEffectivelyVisible))
                AssertInside(view, control, 0);
        }
        finally { window.Close(); }
    }

    private sealed class TestArchiveService : IArchiveService
    {
        public IReadOnlyList<ArchiveCollection> Collections => Array.Empty<ArchiveCollection>();
        public Task<ArchiveAlbumsResult> SearchAsync(string query, string collectionId, string mediaType,
            string? year, string? subject, string? creator, string? language, ArchiveSortMode sort,
            string? cursor, int rows, CancellationToken cancellationToken) => Task.FromResult(new ArchiveAlbumsResult());
        public Task<ArchiveItem> GetItemAsync(string identifier, CancellationToken cancellationToken) =>
            Task.FromResult(new ArchiveItem {
                Formats = new() { "MP3", "FLAC" },
                Tracks = new() { new ArchiveTrack { Title = "Example track", StreamUrl = "https://example.invalid/audio.mp3" } }
            });
        public void Dispose() { }
    }

    private static void AssertInside(Control host, Control control, double inset)
    {
        var origin = control.TranslatePoint(default, host)!.Value;
        string label = control.Name ?? (control as ContentControl)?.Content?.ToString() ?? control.GetType().Name;
        Assert.True(origin.X >= inset - 0.5,
            $"{label} starts outside the left inset: {origin.X}");
        Assert.True(origin.X + control.Bounds.Width <= host.Bounds.Width - inset + 0.5,
            $"{label} exceeds the right inset: {origin.X + control.Bounds.Width} > {host.Bounds.Width - inset}");
    }

    private sealed class TestBrowserContext : IJukeboxMediaBrowserContext
    {
        // No files are created; initialization follows the no-cache path.
        public string PluginDataDirectory { get; } = Path.Combine(Path.GetTempPath(), "jukebox-layout-" + Guid.NewGuid().ToString("N"));
        public string? CurrentlyPlayingUrl => null;
        public string? CurrentlyPlayingTitle => null;
        public event Action? CurrentlyPlayingChanged { add { } remove { } }
        public void PlayNow(PlayRequest request) { }
        public void ReplaceQueueAndPlay(IEnumerable<PlayRequest> queue, string activeSource) { }
        public void Log(string message) { }
        public void AddToQueue(PlayRequest request) { }
        public void AddRangeToQueue(IEnumerable<PlayRequest> requests) { }
        public void UpdateTrackUrl(string originalUrl, string resolvedUrl) { }
        public Task<bool> ShowConfirmAsync(string title, string message) => Task.FromResult(false);
    }
}
