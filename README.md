# Jukebox

<img width="3840" height="2040" alt="image" src="https://github.com/user-attachments/assets/b22df7a3-8f9e-491c-b51e-f8bcca36e89b" />


A cross-platform desktop media player built with Avalonia UI.

## Dependencies

This application requires:
- External, unmanaged native libraries (`bass.dll` + plugins, `libmpv-2.dll`, `vgm-player_Win64.dll` + cores, and ProjectM assets).
- Local custom forks of `Avalonia.Controls.DataGrid` and `Avalonia.Controls.TreeDataGrid`.

For setup and installation instructions for all dependencies, see [DEPENDENCIES.md](DEPENDENCIES.md) and [`lib/README.md`](lib/README.md).

## Features

- **Audio Playback**: Plays standard audio files (MP3, FLAC, WAV, OGG, M4A, WMA) using ManagedBass.
- **VGM Emulation**: Emulates and plays VGM, VGZ, and VGX video game music files using Valley Bell's native `libvgm` player core.
- **ZIP Playback**: Supports playing audio files directly from compressed `.zip` archives.
- **Video Playback**: Plays video files (MP4, MKV, AVI, WEBM) using a custom libmpv wrapper rendering directly to an OpenGL context.
- **Online Radio Browser**: Query, filter, and search thousands of global online radio stations powered by the community-driven Radio-Browser API.
- **Transient Previews**: Listening to radio browser stations creates a transient "Now Playing" slot in your Radio playlist tab rather than permanently cluttering it. Includes an inline **＋ Add to Playlist** pill button to promote the station permanently.
- **Audio Equalizer**: 10-band peaking equalizer for custom sound tuning (via BASS_FX PeakEQ) with saved presets.
- **Visualizations**: Optional music visualizations via projectM (loaded dynamically via reflection) with a picker containing 10,000+ milkdrop presets, favorites, and a customizable randomizer.

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

- **Windows:** place required library files (`bass.dll`, `libmpv-2.dll`, `vgm-player_Win64.dll`, etc.) in a `lib/` directory next to the executable.
- **Linux:** install dependencies via package manager (`sudo apt install libmpv-dev`) and place companion native libraries in `lib/`.

The ProjectM native library (`libprojectM`) is provided by the `JukeboxVisualizations` companion project. Download the ProjectM folder and place it in the root of the Jukebox project.

---

## Command-Line Switches

All switches are case-insensitive. Values are case-sensitive.

| Switch | Value | Description |
|--------|-------|-------------|
| `-light` | — | Use light theme on startup |
| `-dark` | — | Use dark theme on startup |
| `-playlistlogo` | `[file path]` | Render an image logo above the playlist |
| `-random` | — | Enable random/shuffle playback |
| `-hidecontrols` | — | Enable auto-hide mode on the bottom control bar |
| `-nocontrols` | — | Fully disable/hide control UI panels and keyboard shortcuts |
| `-novisualizer` | — | Force visualizer to remain off regardless of availability |
| `-showplaying` | `[timeout seconds]?` | Display the OSD "Now Playing" banner with an optional timeout duration |
| `-randompreset` | `[interval seconds]?` | Enable the visualizer preset randomizer with an optional interval (10-60) |
| `-volume` | `[0-100]` | Set the initial startup volume |
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

# Light theme, random shuffle, stay on top, custom title, show OSD banner for 5 seconds
Jukebox.exe -light -random -stayontop -title "Retro Jukebox" -showplaying 5
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
        IsControlsDisabled="False"
        IsVisualizerDisabled="False"
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
| `IsControlsDisabled` | `bool` | `false` | Fully disable UI controls (playlist, EQ, transport, hotkeys) |
| `IsVisualizerDisabled` | `bool` | `false` | Force visualizer to remain off |

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
        await vm.PlaylistViewModel.ProcessAndAddFilesAsync(new List<string> { "song1.mp3", "song2.flac" });
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

- **`Mpv/MpvNative.cs`** — P/Invoke declarations for libmpv C functions. Includes a `NativeLibrary.SetDllImportResolver` that searches for `libmpv-2.dll` / `libmpv.so.2` / `libmpv.2.dylib` per platform.
- **`Mpv/MpvContext.cs`** — High-level wrapper: `Initialize()`, `LoadFile()`, `Play()` / `Pause()` / `Stop()`, `SeekAbsolute()`, `SetVolume()`, `ObserveProperty()`. Owns the mpv handle, event thread, and render context. Implements `IDisposable`.
- **`Views/MpvView.cs`** — `OpenGlControlBase` subclass. Creates the render context in `OnOpenGlRender`, sets the update callback, and renders frames. `MpvContext` styled property binds to the VM.
- **`ViewModels/JukeboxViewModel.Playback.cs`** — Core VM engine coordinating routing to BASS, VGM, or MPV playback.

### Render Context and Layout

`MpvView` is an `OpenGlControlBase` rendering into Avalonia's GL context, rather than using a native window host. Side panels (Playlist, Visualizer Picker), transport bar, and EQ overlay are normal XAML siblings of `ContentView` rendering on top via standard Z-order. 

### Single OpenGL Control

`ContentView` uses a `ContentControl` (`MediaHost`) that swaps between `MpvView` (video mode) and `ProjectMControl` (audio mode). Only one `OpenGlControlBase` is in the visual tree at a time to prevent context conflicts.

### Close Sequence

1. `JukeboxView.CloseAsync` calls `ContentView.DetachMediaHost()` to remove `MpvView` from the visual tree.
2. `OnOpenGlDeinit` fires to perform GL context cleanup.
3. `DisposePlaybackAsync` triggers `DisposeMpvAsync` to clear the update callback, free the render context, and terminate the mpv handle.

---

## Troubleshooting

### "Video playback is unavailable. MPV failed to initialize."

If the player fails to load `libmpv-2.dll`, verify:
- The file is in the `lib/` directory.
- Review the application output window for `[MPV]` trace statements.

---

## See Also

- [DEPENDENCIES.md](DEPENDENCIES.md) — Native dependencies setup guide.
- [ARCHITECTURE.md](ARCHITECTURE.md) — Detailed document describing native lifecycles, threading constraints, disposal rules, and MPV integration.

## Credits

This project makes use of the following libraries, APIs, and engines:
- [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) — Cross-platform UI framework.
- [libmpv](https://github.com/mpv-player/mpv) — Video playback engine.
- [BASS Audio Library](https://www.un4seen.com/) — Audio playback and DSP processing.
- [libvgm](https://github.com/ValleyBell/libvgm) — Video game music (VGM/VGZ/VGX) emulation and player core.
- [projectM](https://github.com/projectM-visualizer/projectm) — OpenGL music visualization engine.
- [Radio Browser API](https://www.radio-browser.info/) — Community-driven global online radio directory service.
- [TagLib#](https://github.com/mono/taglib-sharp) — Metadata scanning library.
