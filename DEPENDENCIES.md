# Jukebox Project Dependencies

This project relies on unmanaged native libraries for audio playback (BASS), video playback (libmpv), and optional visualizations (ProjectM). These dependencies are not included in the repository — you drop them into a single flat `lib/` folder. See [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md) for the licensing of each third-party library.

## Quick start

1. **Build output directory:** after `dotnet build`, your `bin/Debug/net10.0/` (or `bin/Release/net10.0/`) folder will contain `Jukebox.exe` and an empty `lib/` folder.
2. **Populate `lib/`** with the native binaries listed in [`lib/README.md`](lib/README.md). The README has download URLs and licensing notes for each file.
3. **Run Jukebox.** At startup, the app scans `lib/` and shows a clear error dialog if any required libraries are missing — listing exactly what's missing, where to put it, and where to find download instructions.

No scripts to run. No PowerShell execution policy issues. Just download the files, drop them in `lib/`, and run.

## Standardized layout

All drop-in files (native runtimes + the optional `JukeboxVisualizations.dll` managed wrapper) live under a single flat `lib/` folder. Windows `.dll` and Linux `.so` files coexist by extension; the loader code picks the right filename per OS at runtime.

```text
<appdir>/                              ← Jukebox.exe + managed assemblies
├── Jukebox.exe
├── Jukebox.dll
├── lib/                               ← ALL drop-in files, flat
│   ├── bass.dll                       (Windows — BASS audio)
│   ├── libbass.so                     (Linux   — BASS audio)
│   ├── libmpv-2.dll                   (Windows — libmpv video)
│   ├── libmpv.so.2                    (Linux   — libmpv video)
│   ├── JukeboxVisualizations.dll      (managed wrapper, optional — visualizer)
│   ├── JukeboxVisualizations.deps.json
│   ├── libprojectM.dll                (Windows — ProjectM visualizer, optional)
│   ├── libprojectM.so.4               (Linux   — ProjectM visualizer, optional)
│   └── glew32.dll                     (Windows — required by libprojectM.dll)
└── ProjectM/                          ← preset data only (no native libs)
    ├── presets/
    │   └── (... .milk files)
    ├── textures/
    └── last_preset.txt                ← runtime-written state
```

The `lib/` folder is intentionally empty in the repository — see `lib/README.md` for the list of required files per platform and where to download each one.

The `ProjectM/` folder contains ONLY preset data. The native `libprojectM` binary AND the `JukeboxVisualizations.dll` managed wrapper both live in `lib/` — all optional drop-in files in one place.

---

## 1. BASS Audio Library (Audio)

The application uses BASS for audio playback and DSP analysis. The native `bass.dll` (Windows) or `libbass.so` (Linux) goes in the `lib/` folder.

### Windows setup:
1. Download the BASS library from the Un4seen website (see links below).
2. Extract `bass.dll` (64-bit version).
3. Place `bass.dll` in the `lib/` folder in the Jukebox build output directory.

### Linux setup:
1. Download `libbass.so` from the Un4seen website.
2. Place `libbass.so` in the `lib/` folder in the Jukebox build output directory.

### How it loads

`JukeboxViewModel.PlaybackBASS.cs::PreloadBassNative()` calls `NativeLibrary.Load("<appdir>/lib/bass.dll")` (or `libbass.so` on Linux) BEFORE `Bass.Init()`. Once loaded into the process, the BASS C# wrapper's internal P/Invoke `LoadLibrary("bass.dll")` finds the already-cached handle, no matter what directory the OS would otherwise search. If the file is missing from `lib/`, falls back to the OS default search path.

---

## 2. libmpv (Video)

Video rendering is handled via a custom P/Invoke wrapper to `libmpv`. The native `libmpv-2.dll` (Windows) or `libmpv.so.2` (Linux) goes in the `lib/` folder.

### Windows setup:
1. Download the `libmpv` Windows build from SourceForge (see links below).
2. Extract the archive (it's a `.7z` — use 7-Zip) and find `libmpv-2.dll` inside.
3. Place `libmpv-2.dll` in the `lib/` folder in the Jukebox build output directory.

### Linux setup:
1. Either download `libmpv.so.2` from a libmpv build and place it in `lib/`, OR
2. Install it via your package manager (`sudo apt install libmpv-dev`) — the loader falls back to the system library path if `lib/` doesn't contain it. (If installed system-wide, the startup check won't flag it as missing.)

### How it loads

`Mpv/MpvNative.cs` registers a `DllImportResolver` that first tries `<appdir>/lib/libmpv-2.dll` (or `libmpv.so.2` on Linux), then falls back to the OS default search path.

---

## 3. ProjectM (Visualizations) — Optional

ProjectM visualizations are an **optional drop-in**. The Jukebox project no longer holds a compile-time reference to the `JukeboxVisualizations` companion assembly — it is discovered at runtime via reflection (`Services/VisualizerRuntime.cs`).

### Enabling visualizations

To enable visualizations, three things must be present in the build output directory:

1. **`JukeboxVisualizations.dll`** (managed wrapper) + its companion `JukeboxVisualizations.deps.json` — in `lib/` alongside the other drop-in files.
2. **`libprojectM` native binary** — `libprojectM.dll` (Windows) or `libprojectM.so.4` (Linux) — in the same `lib/` folder. On Windows, `glew32.dll` must also be in `lib/` (libprojectM depends on it).
3. **`ProjectM/presets/`** folder containing the `.milk` preset files. Textures referenced by presets go in `ProjectM/textures/`.

### Disabling visualizations

Remove (or rename) any of: `lib/JukeboxVisualizations.dll`, `lib/libprojectM.*`, or `ProjectM/presets/`, and restart Jukebox. The visualizer toggle button in the transport bar will be hidden, and audio will play without any ProjectM dependency.

### Where to get the drop-in

Build the `Jukebox-Visualizations` companion repository (see links below) using the included `build.ps1` (Windows) or `build.sh` (Linux/macOS) script. The script:
- Builds the managed wrapper for both `win-x64` and `linux-x64` RIDs.
- Builds libprojectM from source via the `build-natives.yml` GitHub Actions workflow.
- Stages `JukeboxVisualizations.dll` + `.deps.json` and the native binaries from `lib/` all into a single `lib/` subfolder, plus preset data into a `ProjectM/` subfolder.
- Produces a ready-to-distribute `Jukebox-Visualizations-dropin.zip`.

Unzip this archive into your Jukebox build output directory. The drop-in's `lib/` contents merge with any `bass.dll` / `libmpv-2.dll` you've already placed there.

### How it works at runtime

`Services/VisualizerRuntime.cs` probes for the drop-in on first access (cached after a successful probe; failed probes are retried on the next call so the user can drop in the folder while the app is running). It checks, in order:

1. `<appdir>/ProjectM/presets/` exists (the preset folder is present).
2. `<appdir>/lib/JukeboxVisualizations.dll` exists (the managed wrapper is in `lib/`).
3. The assembly loads and exposes `JukeboxVisualizations.Controls.ProjectMControl` with the expected `PresetPathProperty`, `StartEngine`, `LoadPreset`, and `FeedPcm` members.

If all three succeed, `IsVisualizerAvailable` becomes `true` and the visualizer button appears. Otherwise, the button stays hidden and the audio path is unaffected.

The native `libprojectM.dll` / `libprojectM.so.4` is loaded by `JukeboxVisualizations.dll`'s `Native/ProjectMNative.cs` from the same `<appdir>/lib/` folder — flat, alongside every other drop-in file.

---

## 4. libvgm (VGM Emulation)

The application uses Valley Bell's `libvgm` to emulate and play VGM, VGZ, and VGX files. The unmanaged native shim files must be dropped into the `lib/` folder.

### Windows setup:
1. Compile Valley Bell's `libvgm` with the flat C API shim wrapper (`vgm-player`).
2. Copy `vgm-player_Win64.dll`, `vgm-emu_Win64.dll`, and `vgm-utils_Win64.dll` to the `lib/` folder.

### Linux setup:
1. Compile Valley Bell's `libvgm` flat C API shim on Linux.
2. Copy `libvgm-player.so`, `libvgm-emu.so`, and `libvgm-utils.so` to the `lib/` folder.

### How it loads

`Native/VgmNative.cs::EnsureLoaded()` calls `NativeLibrary.Load()` on the candidate filenames (`vgm-player.dll`, `vgm-player_Win64.dll` on Windows; `libvgm-player.so` on Linux). Once the shim library is loaded, the player loops by rendering 16-bit PCM and feeding it to a BASS push stream for playback and visualization.

---

## 5. Avalonia Controls Project Forks

The Jukebox solution references custom, local forks of the Avalonia DataGrid and TreeDataGrid repositories. These must exist as folders adjacent to the main `Jukebox` project directory:

* **DataGrid Fork:** Expected at `../Avalonia.Controls.DataGrid/`
* **TreeDataGrid Fork:** Expected at `../Avalonia.Controls.TreeDataGrid/`

The solution will fail to build if these custom fork projects are missing. Ensure they are cloned into their respective directories.

---

## Project Links and Downloads

* **BASS (Un4seen Developments):** [https://www.un4seen.com/](https://www.un4seen.com/)
* **libmpv Builds (Windows):** [https://sourceforge.net/projects/mpv-player-windows/files/libmpv/](https://sourceforge.net/projects/mpv-player-windows/files/libmpv/)
* **libvgm (Valley Bell):** [https://github.com/ValleyBell/libvgm](https://github.com/ValleyBell/libvgm)
* **Jukebox-Visualizations Repository:** [https://github.com/RobG66/Jukebox-Visualizations](https://github.com/RobG66/Jukebox-Visualizations)
* **Avalonia Controls DataGrid Fork:** [https://github.com/RobG66/Avalonia.Controls.DataGrid](https://github.com/RobG66/Avalonia.Controls.DataGrid)
* **Avalonia Controls TreeDataGrid Fork:** [https://github.com/RobG66/Avalonia.Controls.TreeDataGrid](https://github.com/RobG66/Avalonia.Controls.TreeDataGrid)

