# Jukebox

A cross-platform Avalonia desktop media player with one host-owned play queue and drop-in media-browser plugins.

## Projects

```text
                              Main desktop application
Jukebox.Plugin.Abstractions/          Host/plugin contract
Jukebox.slnx                          Solution
```

Concrete plugins are developed outside this repository. Jukebox has no
compile-time references to them and runs normally when none are installed.

Media-browser plugins discover remote media and return normalized `PlayRequest` items. The host owns the Play Queue, saved playlists, playback order, persistence, and playback engines.

## Requirements

- .NET 10 SDK
- Avalonia DataGrid and TreeDataGrid forks checked out beside this repository:
  - `../Avalonia.Controls.DataGrid/`
  - `../Avalonia.Controls.TreeDataGrid/`
- Native playback libraries under `lib/`; see [DEPENDENCIES.md](DEPENDENCIES.md)

## Build and run

```powershell
dotnet build Jukebox.slnx
dotnet run --project Jukebox
```

For distributable packages, run `.\build.ps1`. The `publish/win-x64` and
`publish/linux-x64` packages include the .NET runtime and do not include the
native `lib/` folder; provide that folder separately when installing playback
libraries. `publish/win-x64-lite` remains the optional framework-dependent
Windows package.

Installed plugin binaries under `plugins/<PluginName>/` are copied to
the application output. They are installation artifacts, not projects in the
Jukebox solution.

## Application layout

- A persistent 64-pixel navigation rail contains Queue, Saved Playlists, and installed browser plugins.
- Queue and Saved Playlists open the compact host-owned media panel.
- A selected plugin uses the full remaining content surface inside the Jukebox window.
- The transport bar remains available while browsing.
- Escape navigates browser → last host panel → closed media surface.

## Queue and playlist ownership

- `PlayQueue` is the only runtime playback collection.
- Saved playlists are independent persisted copies.
- Playing a saved playlist copies it into the queue.
- Editing or deleting a saved playlist does not mutate active playback.
- Plugins may replace or append to the host queue through `IJukeboxMediaBrowserContext`; they do not own playlists.

## Plugin framework

`PluginLoader` scans `plugins/*/*.dll`, creates each `IJukeboxMediaBrowser`, supplies an `IJukeboxMediaBrowserContext`, and hosts the plugin's `UserControl` in the main browser surface.

The context exposes host-owned operations:

- `PlayNow`
- `ReplaceQueueAndPlay`
- `AddToQueue`
- `AddRangeToQueue`
- stable-source URL update and resolution support

Plugins may provide a branded bitmap through `IconUri` or themeable vector path data through `IconPathData`. The host controls rail sizing, selection, and fallback presentation.

To add a plugin:

1. Create a .NET class library referencing `Jukebox.Plugin.Abstractions`.
2. Implement `IJukeboxMediaBrowser`.
3. Return an embedded `UserControl` from `CreateView()`.
4. Put the plugin and its dependencies in one `plugins/<PluginName>/` folder.
5. Restart Jukebox.

No main-application reference to the concrete plugin is required.

## Documentation

- [README.md](README.md) — user-facing features and operation
- [ARCHITECTURE.md](ARCHITECTURE.md) — internal design and native lifecycles
- [EMBEDDING.md](EMBEDDING.md) — embedding in another Avalonia application
- [DEPENDENCIES.md](DEPENDENCIES.md) — native and local build dependencies
- [Jukebox_refactor_plan_current_status.md](Jukebox_refactor_plan_current_status.md) — current implementation status and remaining verification
