using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Jukebox.Models;
using Jukebox.Mpv;
using Jukebox.Services;
using Jukebox.ViewModels;
using Jukebox.Views;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Jukebox.Tests;

public sealed class LifecycleAndPlaylistTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jukebox-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TestDialogs _dialogs = new();

    public LifecycleAndPlaylistTests()
    {
        Directory.CreateDirectory(_directory);
        PathProvider.Override(new TestPaths(_directory));
    }

    [Fact]
    public async Task PendingSwitchRejectsStaleGridEditsAndAutosave()
    {
        var playlist = await CreatePlaylistAsync();
        var gate = Field<SemaphoreSlim>(playlist, "_libraryPlaylistSwitchGate");
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        playlist.SelectedLibraryPlaylist = "B";
        Assert.True(playlist.IsLibraryPlaylistBusy);
        await playlist.RemoveLibrarySelectedCommand.ExecuteAsync(new[] { playlist.LibraryPlaylist[0] });
        await playlist.AutoSaveCurrentPlaylistAsync();
        Assert.Equal(new[] { "B first", "B second" }, (await ReadPlaylistAsync("B")).Tracks.Select(t => t.DisplayName));
        gate.Release();
        await WhenIdleAsync(playlist);
        Assert.Equal("B first", playlist.LibraryPlaylist[0].DisplayName);
    }

    [Fact]
    public async Task RapidSwitchesLoadTheLatestRequestedPlaylist()
    {
        var playlist = await CreatePlaylistAsync();
        var gate = Field<SemaphoreSlim>(playlist, "_libraryPlaylistSwitchGate");
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        playlist.SelectedLibraryPlaylist = "B";
        playlist.SelectedLibraryPlaylist = "C";
        gate.Release();
        await WhenIdleAsync(playlist);
        Assert.Equal("C", playlist.SelectedLibraryPlaylist);
        Assert.Equal("C first", playlist.LibraryPlaylist[0].DisplayName);
        Assert.Equal("B first", (await ReadPlaylistAsync("B")).Tracks[0].DisplayName);
    }

    [Fact]
    public async Task SelectionDuringRenameSavesTheLoadedPlaylist()
    {
        var playlist = await CreatePlaylistAsync();
        var rename = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dialogs.RenameResult = rename.Task;
        var edit = playlist.RenameLibrarySelectedCommand.ExecuteAsync(new[] { playlist.LibraryPlaylist[0] });
        playlist.SelectedLibraryPlaylist = "B";
        rename.SetResult("renamed Default");
        await edit;
        await WhenIdleAsync(playlist);
        Assert.Equal("renamed Default", (await ReadPlaylistAsync("Default")).Tracks[0].DisplayName);
        Assert.Equal("B first", (await ReadPlaylistAsync("B")).Tracks[0].DisplayName);
        Assert.Equal("B first", playlist.LibraryPlaylist[0].DisplayName);
    }

    [Fact]
    public async Task CopyToAnExplicitPlaylistWaitsForASwitch()
    {
        var playlist = await CreatePlaylistAsync();
        var gate = Field<SemaphoreSlim>(playlist, "_libraryPlaylistSwitchGate");
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        playlist.SelectedLibraryPlaylist = "B";
        var copy = playlist.CopySelectedToPlaylistCommand.ExecuteAsync(new CopyToPlaylistRequest(
            "B", new[] { new JukeboxTrack { DisplayName = "copied", FilePath = "copied.mp3", IsTagged = true } }));
        Assert.False(copy.IsCompleted);
        gate.Release();
        await copy;
        await WhenIdleAsync(playlist);
        Assert.Equal(new[] { "B first", "B second", "copied" }, playlist.LibraryPlaylist.Select(t => t.DisplayName));
        Assert.Equal("copied", (await ReadPlaylistAsync("B")).Tracks[^1].DisplayName);
    }

    [Fact]
    public async Task FileImportsSaveToTheLoadedPlaylistBeforeASwitch()
    {
        var playlist = await CreatePlaylistAsync();
        string media = Path.Combine(_directory, "imported.mp3");
        await File.WriteAllBytesAsync(media, Array.Empty<byte>(), TestContext.Current.CancellationToken);
        var imported = playlist.ProcessAndAddFilesAsync(new[] { media }, PlaylistTarget.SelectedSavedPlaylist);
        playlist.SelectedLibraryPlaylist = "B";
        await imported;
        await WhenIdleAsync(playlist);
        Assert.Contains((await ReadPlaylistAsync("Default")).Tracks, track => track.FilePath == media);
        Assert.DoesNotContain((await ReadPlaylistAsync("B")).Tracks, track => track.FilePath == media);
    }

    [Fact]
    public async Task DeletionDoesNotResurrectTheDeletedPlaylistDuringASwitch()
    {
        var playlist = await CreatePlaylistAsync();
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dialogs.ConfirmationResult = confirmation.Task;
        var deletion = playlist.DeleteLibraryPlaylistCommand.ExecuteAsync(null);
        playlist.SelectedLibraryPlaylist = "C";
        confirmation.SetResult(true);
        await deletion;
        await WhenIdleAsync(playlist);
        Assert.False(File.Exists(PlaylistPath("Default")));
        Assert.Equal("C", playlist.SelectedLibraryPlaylist);
        Assert.Equal("C first", playlist.LibraryPlaylist[0].DisplayName);
    }

    [Fact]
    public async Task LocalMediaIsNotRecordedUntilItsStartEvent()
    {
        var host = CreateHostWithMpvStub(renderReady: true);
        var playback = host.PlayTrackCommand.ExecuteAsync(Video("local.mp4"));
        Assert.False(playback.IsCompleted);
        Assert.Empty(host.PlaylistViewModel.RecentlyPlayed);
        Invoke(Field<MpvPlaybackEngine>(host, "_mpvEngine"), "SignalPlaybackStarted");
        await playback.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Single(host.PlaylistViewModel.RecentlyPlayed);
        await host.DisposeAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShutdownCancelsUnconfirmedPlaybackWithoutRecordingHistory(bool renderReady)
    {
        var host = CreateHostWithMpvStub(renderReady);
        var playback = host.PlayTrackCommand.ExecuteAsync(Video("unconfirmed.mp4"));
        Assert.False(playback.IsCompleted);
        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await playback;
        Assert.Empty(host.PlaylistViewModel.RecentlyPlayed);
        Assert.Equal(0, _dialogs.ErrorCount);
    }

    [Fact]
    public async Task SupersedingALocalOpenCannotRecordTheOldTrack()
    {
        var host = CreateHostWithMpvStub(renderReady: true);
        var first = host.PlayTrackCommand.ExecuteAsync(Video("first.mp4"));
        var second = host.PlayTrackCommand.ExecuteAsync(Video("second.mp4"));
        await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Invoke(Field<MpvPlaybackEngine>(host, "_mpvEngine"), "SignalPlaybackStarted");
        await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("second.mp4", Assert.Single(host.PlaylistViewModel.RecentlyPlayed).Track.FilePath);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task ShutdownCancelsAStalledHttpOpenBeforeDisposingTheEngine()
    {
        using var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        server.Start();
        int port = ((System.Net.IPEndPoint)server.LocalEndpoint).Port;
        var host = new JukeboxViewModel(new TestOsd(), _dialogs);
        var bass = Field<BassPlaybackEngine>(host, "_bassEngine");
        // No BASS device is initialized. The server never returns headers, so
        // the engine remains in managed HTTP I/O until shutdown cancels it.
        SetField(bass, "<IsAvailable>k__BackingField", true);
        var playback = host.PlayTrackCommand.ExecuteAsync(Video($"http://127.0.0.1:{port}/live"));
        using var connection = await server.AcceptTcpClientAsync(TestContext.Current.CancellationToken)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await playback;
        Assert.Empty(host.PlaylistViewModel.RecentlyPlayed);
        Assert.Equal(0, _dialogs.ErrorCount);
    }

    [Fact]
    public async Task ShutdownWaitsForOutstandingNativeOperations()
    {
        var host = new JukeboxViewModel(new TestOsd(), _dialogs);
        var nativeOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Field<HashSet<Task>>(host, "_pendingPlaybackOperations").Add(nativeOperation.Task);
        var disposal = host.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        nativeOperation.SetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FullDisposalWaitsForAnAlreadyStartedPlaybackDisposal()
    {
        var host = new JukeboxViewModel(new TestOsd(), _dialogs);
        var nativeOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Field<HashSet<Task>>(host, "_pendingPlaybackOperations").Add(nativeOperation.Task);
        var playbackDisposal = host.DisposePlaybackAsync();
        Assert.Same(playbackDisposal, host.DisposePlaybackAsync());
        var fullDisposal = host.DisposeAsync().AsTask();
        Assert.False(fullDisposal.IsCompleted);
        nativeOperation.SetResult();
        await Task.WhenAll(playbackDisposal, fullDisposal).WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PlaybackCommandsAfterDisposalDoNotOpenMediaOrChangeTheQueue()
    {
        var host = new JukeboxViewModel(new TestOsd(), _dialogs);
        await host.DisposeAsync();
        await host.PlayTrackCommand.ExecuteAsync(Video("after-dispose.mp4"));
        await host.PlaySavedTrackNowCommand.ExecuteAsync(Video("saved-after-dispose.mp4"));
        Assert.Empty(host.PlaylistViewModel.PlayQueue);
        Assert.Empty(host.PlaylistViewModel.RecentlyPlayed);
        Assert.Equal(0, _dialogs.ErrorCount);
    }

    [Fact]
    public async Task ShutdownFlushesEveryQueuedHistorySave()
    {
        var host = new JukeboxViewModel(new TestOsd(), _dialogs);
        var gate = Field<SemaphoreSlim>(host.PlaylistViewModel, "_historySaveGate");
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        host.PlaylistViewModel.RecordRecentlyPlayed(Video("first.mp4"));
        host.PlaylistViewModel.RecordRecentlyPlayed(Video("second.mp4"));
        var disposal = host.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        gate.Release();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var json = await File.ReadAllTextAsync(Path.Combine(_directory, "Cache", "PlaybackHistory.json"), TestContext.Current.CancellationToken);
        var history = JsonSerializer.Deserialize<PlaybackHistoryDto>(json)!;
        Assert.Equal(new[] { "second.mp4", "first.mp4" }, history.Entries.Select(e => e.Track.FilePath));
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(1.25, -1920, 0)]
    [InlineData(1.5, 0, -1080)]
    [InlineData(2, -1920, -1080)]
    public void BrowserWindowIncludingDecorationsFitsTheWorkingArea(double scale, int x, int y)
    {
        var work = new PixelRect(x, y, 1366, 768);
        var decoration = new Size(16 / scale, 39 / scale);
        var result = JukeboxView.ConstrainToWorkingArea(new Size(2000, 1000), decoration,
            work, scale, new PixelPoint(work.Right - 10, work.Bottom - 10));
        Assert.True(result.Position.X >= work.X);
        Assert.True(result.Position.Y >= work.Y);
        Assert.True(result.Position.X + Math.Ceiling((result.ClientSize.Width + decoration.Width) * scale) <= work.Right);
        Assert.True(result.Position.Y + Math.Ceiling((result.ClientSize.Height + decoration.Height) * scale) <= work.Bottom);
    }

    private async Task<JukeboxPlaylistViewModel> CreatePlaylistAsync()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "Playlists", "Library"));
        foreach (string name in new[] { "Default", "B", "C" })
            await File.WriteAllTextAsync(PlaylistPath(name), JsonSerializer.Serialize(new SavedPlaylistDto
            {
                Name = name,
                Tracks = new() {
                    new SavedTrackDto { DisplayName = name + " first", FilePath = name + "1.mp3", IsTagged = true },
                    new SavedTrackDto { DisplayName = name + " second", FilePath = name + "2.mp3", IsTagged = true }
                }
            }), TestContext.Current.CancellationToken);
        var playlist = new JukeboxPlaylistViewModel(_dialogs);
        await playlist.InitializeAsync();
        return playlist;
    }

    private JukeboxViewModel CreateHostWithMpvStub(bool renderReady)
    {
        // The zero-handle context exercises the real supervisor and cancellation
        // paths without loading libmpv or opening an audio/video device.
        var host = new JukeboxViewModel(new TestOsd(), _dialogs);
        var engine = Field<MpvPlaybackEngine>(host, "_mpvEngine");
        var context = new MpvContext();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (renderReady) ready.SetResult();
        SetField(context, "_renderContextReadyTcs", ready);
        SetField(engine, "_mpv", context);
        SetField(engine, "<IsAvailable>k__BackingField", true);
        return host;
    }

    private static async Task WhenIdleAsync(JukeboxPlaylistViewModel playlist)
    {
        if (!playlist.IsLibraryPlaylistBusy) return;
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(playlist.IsLibraryPlaylistBusy) && !playlist.IsLibraryPlaylistBusy)
                idle.TrySetResult();
        }
        playlist.PropertyChanged += Changed;
        try
        {
            if (!playlist.IsLibraryPlaylistBusy) idle.TrySetResult();
            await idle.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally { playlist.PropertyChanged -= Changed; }
    }

    private string PlaylistPath(string name) => Path.Combine(_directory, "Playlists", "Library", name + ".json");
    private async Task<SavedPlaylistDto> ReadPlaylistAsync(string name) =>
        JsonSerializer.Deserialize<SavedPlaylistDto>(await File.ReadAllTextAsync(PlaylistPath(name), TestContext.Current.CancellationToken))!;
    private static JukeboxTrack Video(string path) => new() { DisplayName = path, FilePath = path };
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void SetField(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Invoke(object target, string method) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);

    public void Dispose()
    {
        PathProvider.Reset();
        string fullPath = Path.GetFullPath(_directory);
        string testPrefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "jukebox-tests-");
        if (!fullPath.StartsWith(testPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test data is outside the temporary test directory.");
        Directory.Delete(fullPath, recursive: true);
    }

    private sealed class TestPaths(string root) : IPathProvider
    {
        public string NativeLibDirectory => Path.Combine(root, "lib");
        public string SettingsDirectory => Path.Combine(root, "Settings");
        public string EqSettingsFile => Path.Combine(SettingsDirectory, "eq.json");
        public string PlaylistsDirectory => Path.Combine(root, "Playlists");
        public string CacheDirectory => Path.Combine(root, "Cache");
    }

    private sealed class TestDialogs : IUserDialogService
    {
        public int ErrorCount { get; private set; }
        public Task<bool> ConfirmationResult { get; set; } = Task.FromResult(true);
        public Task<string?> RenameResult { get; set; } = Task.FromResult<string?>(null);
        public Task ShowErrorAsync(string title, string message) { ErrorCount++; return Task.CompletedTask; }
        public Task ShowWarningAsync(string title, string message) => Task.CompletedTask;
        public Task ShowInfoAsync(string title, string message) => Task.CompletedTask;
        public Task<bool> ShowConfirmAsync(string title, string message, string confirmText = "Yes", string cancelText = "Cancel", DialogIconTheme icon = DialogIconTheme.Question, string? detail = null) => ConfirmationResult;
        public Task<(string? Name, bool SaveAsDefault)> ShowTextInputAsync(string title, string prompt, string placeholder = "", Func<string, (bool IsValid, string ErrorMessage)>? validator = null, string okButtonText = "OK", bool showDefaultCheckbox = false, string defaultCheckboxText = "Save as default startup playlist") => Task.FromResult<(string?, bool)>((null, false));
        public Task<string?> ShowRenameAsync(string currentName, Func<string, (bool IsValid, string ErrorMessage)>? validator = null, string title = "Rename Profile", string prompt = "Enter new name for the profile:", bool isFileName = true) => RenameResult;
    }

    private sealed class TestOsd : IShowPlayingService
    {
        public bool IsVisible => false;
        public double Opacity => 0;
        public string Text => "";
        public event EventHandler<ShowPlayingEventArgs>? Changed { add { } remove { } }
        public Task ShowAsync(string text, double? holdSeconds = null, bool alwaysShow = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Hide() { }
    }
}
