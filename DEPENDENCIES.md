# Dependencies

Jukebox depends on several unmanaged native libraries and local project forks. Host playback libraries go in `lib/`; the optional ProjectM visualizer is a self-contained package under `plugins/Avalonia.ProjectM/`. This document lists every dependency, where to get it, and how it loads at runtime.

For license information, see [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md).

---

## Directory layout

Host playback files live under a flat `lib/` folder next to `Jukebox.exe`. Windows `.dll` and Linux `.so` files coexist by extension. ProjectM files do not use this folder.

```text
<appdir>/
├── Jukebox.exe
├── Jukebox.dll
├── lib/                               ← host playback runtimes
│   ├── bass.dll                       (Windows — BASS audio)
│   ├── bass_fx.dll                    (Windows — BASS FX add-on, for EQ)
│   ├── bassflac.dll                   (Windows — BASS FLAC plugin, optional)
│   ├── bass_aac.dll                   (Windows — BASS AAC plugin, optional)
│   ├── bassopus.dll                   (Windows — BASS OPUS plugin, optional)
│   ├── basshls.dll                    (Windows — BASS HLS plugin, optional)
│   ├── libbass.so                     (Linux   — BASS audio)
│   ├── libbass_fx.so                  (Linux   — BASS FX add-on, for EQ)
│   ├── libbassflac.so                 (Linux   — BASS FLAC plugin, optional)
│   ├── libbass_aac.so                 (Linux   — BASS AAC plugin, optional)
│   ├── libbassopus.so                 (Linux   — BASS OPUS plugin, optional)
│   ├── libbasshls.so                  (Linux   — BASS HLS plugin, optional)
│   ├── libmpv-2.dll                   (Windows — libmpv video)
│   ├── libmpv.so.2                    (Linux   — libmpv video)
│   ├── vgm-player_Win64.dll           (Windows — libvgm VGM emulation)
│   ├── vgm-emu_Win64.dll              (Windows — libvgm emulator core)
│   ├── vgm-utils_Win64.dll            (Windows — libvgm utilities)
│   ├── libvgm-player.so               (Linux   — libvgm VGM emulation)
│   ├── libvgm-emu.so                  (Linux   — libvgm emulator core)
│   └── libvgm-utils.so                (Linux   — libvgm utilities)
└── plugins/
    ├── <PluginName>/                     (optional managed plugin)
    └── Avalonia.ProjectM/
        ├── Avalonia.ProjectM.dll
        ├── Avalonia.ProjectM.deps.json
        ├── libprojectM.dll            (Windows)
        ├── glew32.dll                 (Windows)
        ├── libprojectM.so.4           (Linux)
        ├── libprojectM.dylib          (macOS, when supported)
        ├── libprojectM-LICENSE.txt
        └── ProjectM/
            ├── presets/
            ├── textures/
            └── current_preset/
```

The `lib/` folder is created empty by the build. At startup, Jukebox scans it and shows a dialog if any required libraries are missing — listing exactly what's missing and where to find download instructions.

Media-browser plugins are managed assemblies and their managed dependencies. Each browser stays in its own `plugins/<PluginName>/` folder. `Avalonia.ProjectM` is a separate optional visualizer package with native libraries and assets; it is not loaded as a media browser.

---

## 1. BASS Audio Library

Audio playback, DSP, and PCM data extraction.

| Platform | Files | Required |
|----------|-------|----------|
| Windows | `bass.dll`, `bass_fx.dll` | Yes |
| Windows | `bassflac.dll`, `bass_aac.dll`, `bassopus.dll`, `basshls.dll` | No (Required for FLAC/AAC/OPUS/Radio playback) |
| Linux | `libbass.so`, `libbass_fx.so` | Yes |
| Linux | `libbassflac.so`, `libbass_aac.so`, `libbassopus.so`, `libbasshls.so` | No (Required for FLAC/AAC/OPUS/Radio playback) |

**Download:** [https://www.un4seen.com/](https://www.un4seen.com/)

- `bass.dll` / `libbass.so` — the core BASS library (64-bit version)
- `bass_fx.dll` / `libbass_fx.so` — the BASS FX add-on, used for the 10-band peaking equalizer
- `bassflac.dll` / `libbassflac.so` — FLAC format plugin, required for playing local `.flac` files
- `bass_aac.dll` / `libbass_aac.so` — AAC format plugin, required for playing AAC files/streams
- `bassopus.dll` / `libbassopus.so` — OPUS format plugin, required for playing OPUS files/streams
- `basshls.dll` / `libbasshls.so` — HLS stream plugin, required for HTTP Live Streaming (e.g. `.m3u8` radio streams)

**How it loads:** `BassPlaybackEngine.PreloadBassNative()` calls `NativeLibrary.Load("<appdir>/lib/bass.dll")` before `Bass.Init()`. Once loaded into the process, the BASS C# wrapper's internal P/Invoke finds the cached handle. The `bass_fx` binary is preloaded the same way in `PreloadBassFxNative()`.

---

## 2. libmpv

Video playback, rendered into Avalonia's OpenGL context.

| Platform | File | Required |
|----------|------|----------|
| Windows | `libmpv-2.dll` | Yes (for video) |
| Linux | `libmpv.so.2` | Yes (for video, or install system-wide) |

**Download (Windows):** [https://sourceforge.net/projects/mpv-player-windows/files/libmpv/](https://sourceforge.net/projects/mpv-player-windows/files/libmpv/)

The archive is a `.7z` — use 7-Zip to extract and find `libmpv-2.dll` inside.

**Linux alternative:** Install via package manager (`sudo apt install libmpv-dev`). The loader falls back to the system library path if `lib/` doesn't contain the file.

**How it loads:** `Mpv/MpvNative.cs` registers a `DllImportResolver` that first tries `<appdir>/lib/libmpv-2.dll` (or `libmpv.so.2` on Linux), then falls back to the OS default search path.

---

## 3. libvgm

VGM/VGZ/VGX video game music file emulation.

| Platform | Files | Required |
|----------|-------|----------|
| Windows | `vgm-player_Win64.dll`, `vgm-emu_Win64.dll`, `vgm-utils_Win64.dll` | Yes (for VGM files) |
| Linux | `libvgm-player.so`, `libvgm-emu.so`, `libvgm-utils.so` | Yes (for VGM files) |

**Source:** [https://github.com/RobG66/libvgm](https://github.com/RobG66/libvgm) (fork of Valley Bell's [libvgm](https://github.com/ValleyBell/libvgm))

You need to compile libvgm with the flat C API shim wrapper (`vgm-player`). The shim wraps the C++ `PlayerA` class into P/Invokable C functions.

**How it loads:** `Native/VgmNative.cs::EnsureLoaded()` calls `NativeLibrary.Load()` on the candidate filenames, then resolves function exports directly into delegates. The player renders 16-bit PCM on a background thread and feeds it into a BASS push stream.

---

## 4. ProjectM (Visualizations) — Optional

Music visualizations via projectM milkdrop presets.

| Platform | Files | Required |
|----------|-------|----------|
| Windows | `Avalonia.ProjectM.dll`, `libprojectM.dll`, `glew32.dll` | No |
| Linux | `Avalonia.ProjectM.dll`, `libprojectM.so.4` | No |
| macOS | `Avalonia.ProjectM.dll`, `libprojectM.dylib` | No |

**Managed wrapper:** `Avalonia.ProjectM.dll` + `Avalonia.ProjectM.deps.json` in `plugins/Avalonia.ProjectM/`.

**Preset data:** `plugins/Avalonia.ProjectM/ProjectM/presets/` contains `.milk` files; `ProjectM/textures/` contains referenced textures.

**Source:**
- ProjectM: [https://github.com/projectM-visualizer/projectm](https://github.com/projectM-visualizer/projectm)
- Avalonia.ProjectM wrapper: [https://github.com/RobG66/Avalonia.ProjectM](https://github.com/RobG66/Avalonia.ProjectM)

Build the `Avalonia.ProjectM` companion repository using its `build.ps1` (Windows) or `build.sh` (Linux/macOS) script, then place the complete package at `plugins/Avalonia.ProjectM/`.

**How it loads:** the external visualizer plugin probes its own package during initialization. It checks:
1. `<appdir>/plugins/Avalonia.ProjectM/Avalonia.ProjectM.dll` exists
2. The platform's native `libprojectM` library exists beside it; Windows also requires `glew32.dll`
3. `ProjectM/presets/` exists inside the package
4. The assembly exports `Avalonia.ProjectM.Controls.ProjectMControl`

If the checks succeed, the plugin reports `IsAvailable = true` and the visualizer toggle button appears. Otherwise the plugin is skipped and audio plays normally.

**Disabling:** Remove or rename any of the drop-in files and restart. The visualizer button disappears and audio is unaffected.

---

## 5. Avalonia Controls Project Forks

The solution references custom local forks of Avalonia's DataGrid and TreeDataGrid controls. These must exist as sibling directories next to the main `Jukebox` project folder.

| Fork | Expected path | Repository |
|------|---------------|------------|
| DataGrid | `../Avalonia.Controls.DataGrid/` | [https://github.com/RobG66/Avalonia.Controls.DataGrid](https://github.com/RobG66/Avalonia.Controls.DataGrid) |
| TreeDataGrid | `../Avalonia.Controls.TreeDataGrid/` | [https://github.com/RobG66/Avalonia.Controls.TreeDataGrid](https://github.com/RobG66/Avalonia.Controls.TreeDataGrid) |

**Required directory layout:**

```text
parent-folder/
├── Jukebox/                          ← this repository
│   ├── Jukebox.slnx
│   └── Jukebox/
├── Avalonia.Controls.DataGrid/       ← fork, cloned here
│   └── src/Avalonia.Controls.DataGrid/
└── Avalonia.Controls.TreeDataGrid/   ← fork, cloned here
    └── src/Avalonia.Controls.TreeDataGrid/
```

The build will fail if these forks are missing. Clone them into the sibling directories shown above.

---

## Quick reference — all download links

| Dependency | Link |
|------------|------|
| BASS (Un4seen) | [https://www.un4seen.com/](https://www.un4seen.com/) |
| libmpv (Windows builds) | [https://sourceforge.net/projects/mpv-player-windows/files/libmpv/](https://sourceforge.net/projects/mpv-player-windows/files/libmpv/) |
| libvgm (RobG66 fork) | [https://github.com/RobG66/libvgm](https://github.com/RobG66/libvgm) |
| ProjectM | [https://github.com/projectM-visualizer/projectm](https://github.com/projectM-visualizer/projectm) |
| Avalonia.ProjectM wrapper | [https://github.com/RobG66/Avalonia.ProjectM](https://github.com/RobG66/Avalonia.ProjectM) |
| Avalonia.Controls.DataGrid fork | [https://github.com/RobG66/Avalonia.Controls.DataGrid](https://github.com/RobG66/Avalonia.Controls.DataGrid) |
| Avalonia.Controls.TreeDataGrid fork | [https://github.com/RobG66/Avalonia.Controls.TreeDataGrid](https://github.com/RobG66/Avalonia.Controls.TreeDataGrid) |
