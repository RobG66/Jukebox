# Jukebox

A cross-platform desktop media player built with Avalonia UI.

## Dependencies

This application requires:
- External, unmanaged native libraries for audio (`bass.dll` / `libbass.so`) and video (`libmpv-2.dll` / `libmpv.so.2`) playback, dropped into a flat `lib/` folder.
- Local custom forks of `Avalonia.Controls.DataGrid` and `Avalonia.Controls.TreeDataGrid`.
- **Optional** — `JukeboxVisualizations.dll` + `lib/libprojectM.*` + `ProjectM/presets/` for music visualizations.

For setup and installation instructions for all dependencies, see [DEPENDENCIES.md](DEPENDENCIES.md).

## Features

- Audio playback via ManagedBass — works with zero ProjectM dependencies.
- Optional ProjectM (milkdrop) visualizer — activated by dropping the managed wrapper + native binary + preset folder next to the executable.
- Video playback via libmpv (custom P/Invoke wrapper rendering directly to OpenGL context).
- 10-band equalizer with presets.
- Playlist with virtualized metadata tagging.
- Visualizer picker with 10,000+ milkdrop presets, favorites, and randomizer (only when the ProjectM drop-in is present).

## Table of Contents

- [Quick Start](#quick-start)
- [Command-Line Switches](#command-line-switches)
- [Embedding JukeboxControl in Another App](#embedding-jukeboxcontrol-in-another-app)
- [Architecture Overview](#architecture-overview)
- [Troubleshooting](#troubleshooting)
- [See Also](#see-also)

## Quick Start

### Standalone

```bash
Jukebox.exe                          # Launch with defaults
Jukebox.exe -dark -file "C:\Music"   # Dark theme, auto-load folder
Jukebox.exe -?                       # Show help
```

### Native Dependencies

All native runtimes live in a flat `lib/` folder next to `Jukebox.exe`. Windows `.dll` and Linux `.so` files coexist by extension.

- **Windows:** `lib/bass.dll`, `lib/libmpv-2.dll` (drop into `lib/`)
- **Linux:** `lib/libbass.so`, `lib/libmpv.so.2` (drop into `lib/`, OR install `libmpv-dev` via apt as a fallback)

The `lib/` folder is intentionally empty in the repo — populate it by downloading the binaries listed in [`lib/README.md`](lib/README.md). At startup, Jukebox scans `lib/` and shows a clear error dialog if any required libraries are missing, listing exactly what's needed and where to get it.

See [DEPENDENCIES.md](DEPENDENCIES.md) for details and [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md) for licensing.

### Optional: ProjectM Visualizations

Visualizations are entirely optional. The jukebox plays audio out of the box with no ProjectM files present — the visualizer toggle button in the transport bar is hidden when visualizations are not available.

To enable visualizations, three things must be present in the build output directory:

1. **`JukeboxVisualizations.dll`** (managed wrapper) in `lib/` alongside the native runtimes.
2. **`lib/libprojectM.dll`** (Windows) or **`lib/libprojectM.so.4`** (Linux) — alongside the other native runtimes in `lib/`. On Windows, `lib/glew32.dll` is also required (libprojectM depends on it).
3. **`ProjectM/presets/`** folder containing the `.milk` preset files.

Build the [Jukebox-Visualizations](https://github.com/RobG66/Jukebox-Visualizations) companion repo with its `build.ps1` / `build.sh` script — it produces a drop-in zip with the wrapper, native libs, and presets in the correct layout. Unzip into your Jukebox build output directory, then drop in Jukebox's own native runtimes (`bass.dll`, `libmpv-2.dll` / `libbass.so`, `libmpv.so.2`) into the same `lib/` folder.

See [DEPENDENCIES.md](DEPENDENCIES.md) for the precise directory layout.

---

## Command-Line Switches

All switches are case-insensitive. Values are case-sensitive.

| Switch | Value | Description |
|--------|-------|-------------|
| `-light` | — | Use light theme on startup |
| `-dark` | — | Use dark theme on startup |
| `-playlistlogo` | `[file path]` | Render an image logo above the playlist |
| `-random` | — | Enable random/shuffle playback |
| `-hidecontrols` | — | Hide the bottom control bar on startup (auto-hide ON) |
| `-volume` | `[0-100]` | Set the initial volume |
| `-stayontop` | — | Force window always-on-top |
| `-fullscreen` | — | Start in fullscreen |
| `-minimized` | — | Start minimized |
| `-file` | `[path]` | Auto-load file or directory on startup |
| `-loop` | — | Loop playlist continuously |
| `-title` | `[text]` | Override the window title |
| `-?` / `-help` / `--help` / `/?` | — | Show help and exit |

### Examples

```bash
# Dark theme, volume 50, auto-load a folder, loop
Jukebox.exe -dark -volume 50 -file "D:\Music\Playlist" -loop

# Light theme, random shuffle, stay on top, custom title
Jukebox.exe -light -random -stayontop -title "Now Playing"
```

---

## Embedding JukeboxControl in Another App

The `JukeboxControl` is an embeddable `UserControl` that can be hosted in any Avalonia window.

### XAML Embedding

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:jukebox="using:Jukebox.Views"
        Title="My App"
        Width="1280" Height="720">

    <jukebox:JukeboxControl
        InitialFile="{Binding StartupFile}"
        InitialVolume="75"
        IsRandomPlayback="True"
        IsLoopEnabled="False"
        IsAutoHideEnabled="False"
        PlaylistLogo="{Binding LogoPath}" />

</Window>
```

### Styled Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `InitialFile` | `string?` | `null` | File or directory to auto-load on startup |
| `PlaylistLogo` | `string?` | `null` | Path to an image file rendered above the playlist toolbar |
| `InitialVolume` | `int` | `100` | Initial volume (0-100). Also sets `vm.Volume` immediately. |
| `IsRandomPlayback` | `bool` | `false` | Enable random/shuffle playback |
| `IsLoopEnabled` | `bool` | `false` | Loop the playlist continuously |
| `IsAutoHideEnabled` | `bool` | `false` | Auto-hide the transport bar after inactivity |

### Code-Behind Embedding

```csharp
using Jukebox.Views;
using Jukebox.ViewModels;

var control = new JukeboxControl
{
    InitialVolume = 75,
    IsRandomPlayback = true,
};

control.Loaded += async (_, _) =>
{
    if (control.DataContext is JukeboxViewModel vm)
    {
        vm.StorageService = new Jukebox.Services.StorageService(this);
        await vm.PlayMediaFilesAsync(new[] { "song1.mp3", "song2.flac" }, autoPlay: true);
    }
};
```

### StorageService

If your host app needs file-open/folder-open dialogs to work inside the Jukebox:

```csharp
vm.StorageService = new Jukebox.Services.StorageService(hostWindow);
```

---

## Architecture Overview

### Video Playback: MPV (libmpv) via OpenGL

Video is rendered by **libmpv** into Avalonia's OpenGL context via a custom P/Invoke wrapper. No third-party .NET MPV package is used.

- **`Mpv/MpvNative.cs`** — P/Invoke declarations for libmpv C functions. Includes a `NativeLibrary.SetDllImportResolver` that searches `<appdir>/lib/` first for `libmpv-2.dll` / `libmpv.so.2` / `libmpv.2.dylib`, then falls back to the OS default search path.
- **`Mpv/MpvContext.cs`** — High-level wrapper: `Initialize()`, `LoadFile()`, `Play()` / `Pause()` / `Stop()`, `SeekAbsolute()`, `SetVolume()`, `ObserveProperty()`. Owns the mpv handle, event thread, and render context. Implements `IDisposable`.
- **`Views/MpvView.cs`** — `OpenGlControlBase` subclass. Creates the render context in `OnOpenGlRender`, sets the update callback, and renders frames. `MpvContext` styled property binds to the VM.
- **`ViewModels/JukeboxViewModel.PlaybackMPV.cs`** — Partial VM for video playback, using `MpvContext` for all video operations.

### Render Context and Layout

`MpvView` is an `OpenGlControlBase` rendering into Avalonia's GL context, rather than using a native window host. Side panels (Playlist, Visualizer Picker), transport bar, and EQ overlay are normal XAML siblings of `ContentView` rendering on top via standard Z-order. 

### Single OpenGL Control

`ContentView` uses a `ContentControl` (`MediaHost`) that swaps between `MpvView` (video mode), `ProjectMControl` (audio mode + visualizer available), and empty (audio mode + visualizer unavailable). Only one `OpenGlControlBase` is in the visual tree at a time to prevent context conflicts. The `ProjectMControl` is created at runtime via reflection against the optional `JukeboxVisualizations.dll` — see `Services/VisualizerRuntime.cs`. When the assembly (or the `ProjectM` drop-in folder) is absent, the visualizer button is hidden and the MediaHost stays empty during audio playback.

### Close Sequence

1. `JukeboxView.CloseAsync` calls `ContentView.DetachMediaHost()` to remove `MpvView` from the visual tree.
2. `OnOpenGlDeinit` fires to perform GL context cleanup.
3. `DisposePlaybackAsync` triggers `DisposeMpvAsync` to clear the update callback, free the render context, and terminate the mpv handle.

---

## Troubleshooting

### "Video playback is unavailable. MPV failed to initialize."

If the player fails to load `libmpv-2.dll` (Windows) or `libmpv.so.2` (Linux), verify:
- The file is in the `lib/` folder (next to `Jukebox.exe`).
- The `lib/` folder copied to `bin/Debug/net10.0/lib/` (or your build output directory).
- On Linux, you can alternatively install libmpv system-wide via `sudo apt install libmpv-dev` — the loader falls back to the OS search path if `lib/` doesn't contain it.
- Review the application output window for `[MPV]` trace statements.

---

## See Also

- [DEPENDENCIES.md](DEPENDENCIES.md) — Native dependencies setup guide.
- [ARCHITECTURE.md](ARCHITECTURE.md) — Detailed document describing native lifecycles, threading constraints, disposal rules, and MPV integration.

## Credits

This project makes use of the following libraries and engines:
- [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) — Cross-platform UI framework.
- [libmpv](https://github.com/mpv-player/mpv) — Video playback engine.
- [BASS Audio Library](https://www.un4seen.com/) — Audio playback and DSP processing.
- [projectM](https://github.com/projectM-visualizer/projectm) — OpenGL music visualization engine.
- [TagLib#](https://github.com/mono/taglib-sharp) — Metadata scanning library.
