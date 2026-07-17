# Developer Guide: Embedding Jukebox

`JukeboxControl` is an Avalonia `UserControl` that can be hosted inside another desktop application. The host supplies a `JukeboxViewModel`, initializes the backends, and coordinates shutdown.

## Requirements

- .NET 10 and Avalonia UI 12.x
- A project reference to `Jukebox.csproj` or the built Jukebox assemblies
- Native playback libraries under the host output's `lib/` directory
- Optional browser plugins under `plugins/<PluginName>/`
- Optional ProjectM package under `plugins/Avalonia.ProjectM/`
- WGL rendering on Windows; see `Program.cs`

See [DEPENDENCIES.md](DEPENDENCIES.md) for the complete runtime layout.

## Create and host the control

```csharp
using Jukebox.Services;
using Jukebox.ViewModels;
using Jukebox.Views;

var vm = new JukeboxViewModel
{
    InitialVolume = 75,
    IsRandomPlayback = false,
    IsLoopEnabled = false
};

vm.StorageService = new StorageService(hostWindow);

var control = new JukeboxControl
{
    DataContext = vm,
    InitialVolume = 75,
    IsAutoHideEnabled = false,
    IsControlsDisabled = false,
    IsVisualizerDisabled = false
};

hostContent.Content = control;
```

The standalone `JukeboxView` performs initialization from its `Loaded` handler. An embedded host must perform the equivalent once:

```csharp
await vm.InitializeBackendAsync();
await vm.VisualizerViewModel.LoadVisualizersAsync();
await vm.VisualizerViewModel.InitializeAsync();
await vm.EqViewModel.LoadAsync();
```

## JukeboxControl properties

| Property | Type | Default | Purpose |
|---|---:|---:|---|
| `InitialFile` | `string?` | `null` | File or directory imported into the Play Queue at startup |
| `InitialVolume` | `int` | `100` | Startup volume from 0–100 |
| `IsRandomPlayback` | `bool` | `false` | Shuffle the Play Queue |
| `IsLoopEnabled` | `bool` | `false` | Loop the Play Queue |
| `IsAutoHideEnabled` | `bool` | `false` | Auto-hide the transport bar |
| `IsControlsDisabled` | `bool` | `false` | Disable panels, transport controls, and shortcuts |
| `IsVisualizerDisabled` | `bool` | `false` | Disable ProjectM even when installed |
| `IsShowPlayingEnabled` | `bool` | `true` | Enable the Now Playing display |
| `ShowPlayingTimeout` | `int` | `10` | Now Playing hold time in seconds |
| `IsVisualizerRandomizerEnabled` | `bool` | `false` | Auto-cycle ProjectM presets |
| `VisualizerRandomizerIntervalSeconds` | `int` | `10` | Preset interval from 10–60 seconds |

## Queue and saved-playlist APIs

`PlayQueue` is the only collection used by the playback coordinator. `LibraryPlaylist` is the currently selected saved collection and never controls playback directly.

```csharp
using Jukebox.ViewModels;

var playlists = vm.PlaylistViewModel;

// Import local files into the runtime queue.
var queued = await playlists.ProcessAndAddFilesAsync(
    new[] { @"C:\Music", "song.flac" },
    PlaylistTarget.PlayQueue,
    noRecurse: false);

// Explicitly edit the selected saved playlist without changing playback.
var saved = await playlists.ProcessAndAddFilesAsync(
    new[] { "another-song.mp3" },
    PlaylistTarget.SelectedSavedPlaylist,
    noRecurse: false);

// Convenience API: imports into Play Queue by default.
await vm.PlayMediaFilesAsync(
    new[] { "song1.mp3", "song2.flac" },
    autoPlay: true);

// An embedded host must opt in explicitly to editing a saved playlist.
await vm.PlayMediaFilesAsync(
    new[] { "song3.mp3" },
    autoPlay: false,
    PlaylistTarget.SelectedSavedPlaylist);
```

Useful queue operations are exposed by `JukeboxPlaylistViewModel`:

- `ReplacePlayQueue`
- `AppendToPlayQueue`
- `InsertNextInPlayQueue`
- `RemoveFromPlayQueue`
- `ClearPlayQueue`
- `CopyTrack`

## Playback commands

```csharp
vm.PlayCommand.Execute(null);
vm.PauseCommand.Execute(null);
vm.StopCommand.Execute(null);
vm.NextCommand.Execute(null);
vm.PreviousCommand.Execute(null);

if (vm.PlayTrackCommand.CanExecute(vm.PlaylistViewModel.PlayQueue[0]))
{
    vm.PlayTrackCommand.Execute(vm.PlaylistViewModel.PlayQueue[0]);
}
```

## Storage dialogs

File and folder pickers require a `StorageService` tied to the containing window:

```csharp
vm.StorageService = new StorageService(hostWindow);
```

Without it, playback still works but the Add Files and Add Folder buttons cannot open native pickers.

## Drag and drop

The visible host destination determines the target:

- Saved Playlists → selected saved collection and auto-save
- Queue, a browser, or a closed media surface → Play Queue
- Saved-playlist drops never start or interrupt playback
- Queue/browser drops may start the first imported item when idle

## Plugin browsers

The standalone app discovers media browsers before showing the main window. An embedding host that wants browser plugins must perform the same `PluginLoader.LoadAllAsync` and `CreateView` registration used by `App.axaml.cs`.

Plugins are discovery surfaces only. They submit `PlayRequest` values through `IJukeboxMediaBrowserContext`; the Jukebox owns the runtime queue and all saved playlists.

## Shutdown

Call `await vm.DisposeAsync()` exactly once from the host's coordinated close path. Disposal:

- stops playback timers and engines;
- disposes every host-owned browser and cancels active browser work;
- detaches event subscriptions;
- disposes the visualizer view-model.

If the host displays MPV or ProjectM, remove the native media control from the visual tree before disposing the playback context. The standalone `JukeboxView.CloseAsync` is the reference sequence.

## See also

- [ARCHITECTURE.md](ARCHITECTURE.md)
- [DEPENDENCIES.md](DEPENDENCIES.md)
