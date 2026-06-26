# Jukebox Project Dependencies

This project relies on unmanaged native libraries for audio playback (BASS), video playback (libmpv), and optional visualizations (ProjectM). These dependencies are not included in the repository — they are downloaded and verified by the `fetch-natives.ps1` / `fetch-natives.sh` script into a single flat `lib/` folder. See [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md) for the licensing of each third-party library.

## Quick start

```bash
# Windows
.\fetch-natives.ps1

# Linux / macOS
./fetch-natives.sh
```

The script reads `natives.json` (the manifest of URLs + SHA-256 checksums), downloads each asset for the current platform, verifies the checksum, and extracts into `lib/`. It's idempotent — safe to re-run; pass `-Force` / `--force` to re-download everything.

The `natives.json` manifest pins the URL + SHA-256 of each binary. To update a library, edit the manifest (bump URL + sha256), commit the change, and re-run the script. No third-party binaries are ever committed to git history.

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

The `lib/` folder is intentionally empty in the repository — it's a drop-in location populated by a separate setup script (TBD) or by manually copying the third-party native binaries. See `lib/README.md` for the list of required files per platform.

The `ProjectM/` folder contains ONLY preset data. The native `libprojectM` binary AND the `JukeboxVisualizations.dll` managed wrapper both live in `lib/` — all optional drop-in files in one place.

---

## 1. ManagedBass (Audio)

The application uses BASS for audio playback and DSP analysis. The native `bass.dll` (Windows) or `libbass.so` (Linux) goes in the `lib/` folder.

### Windows setup:
1. Download the BASS library from the Un4seen website (see links below).
2. Extract `bass.dll` (64-bit version).
3. Place `bass.dll` in the `lib/` folder at the root of the Jukebox project.

### Linux setup:
1. Download `libbass.so` from the Un4seen website.
2. Place `libbass.so` in the `lib/` folder at the root of the Jukebox project.

### How it loads

`JukeboxViewModel.PlaybackBASS.cs::PreloadBassNative()` calls `NativeLibrary.Load("<appdir>/lib/bass.dll")` (or `libbass.so` on Linux) BEFORE `Bass.Init()`. Once loaded into the process, ManagedBass's internal P/Invoke `LoadLibrary("bass.dll")` finds the already-cached handle, no matter what directory the OS would otherwise search. If the file is missing from `lib/`, falls back to the OS default search path (lets Linux users install libbass system-wide if they prefer).

---

## 2. libmpv (Video)

Video rendering is handled via a custom P/Invoke wrapper to `libmpv`. The native `libmpv-2.dll` (Windows) or `libmpv.so.2` (Linux) goes in the `lib/` folder.

### Windows setup:
1. Download the `libmpv` Windows build from SourceForge (see links below).
2. Extract the archive and copy `libmpv-2.dll` (on some builds it might be named `mpv-2.dll` — if so, rename it).
3. Place `libmpv-2.dll` in the `lib/` folder at the root of the Jukebox project.

### Linux setup:
1. Either download `libmpv.so.2` from a libmpv build (e.g. from the mpv project's releases) and place it in the `lib/` folder, OR
2. Install it via your package manager (`sudo apt install libmpv-dev`) — the loader falls back to the system library path if `lib/` doesn't contain it.

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
- Stages `JukeboxVisualizations.dll` + `.deps.json` and the native binaries from `lib/` all into a single `lib/` subfolder, plus preset data into a `ProjectM/` subfolder.
- Produces a ready-to-distribute `Jukebox-Visualizations-dropin.zip`.

Unzip this archive into your Jukebox build output directory. Then drop the Jukebox's own native runtimes (`bass.dll` / `libbass.so`, `libmpv-2.dll` / `libmpv.so.2`) into the same `lib/` folder.

### How it works at runtime

`Services/VisualizerRuntime.cs` probes for the drop-in on first access (cached after a successful probe; failed probes are retried on the next call so the user can drop in the folder while the app is running). It checks, in order:

1. `<appdir>/ProjectM/presets/` exists (the preset folder is present).
2. `<appdir>/lib/JukeboxVisualizations.dll` exists (the managed wrapper is in `lib/`).
3. The assembly loads and exposes `JukeboxVisualizations.Controls.ProjectMControl` with the expected `PresetPathProperty`, `StartEngine`, `LoadPreset`, and `FeedPcm` members.

If all three succeed, `IsVisualizerAvailable` becomes `true` and the visualizer button appears. Otherwise, the button stays hidden and the audio path is unaffected.

The native `libprojectM.dll` / `libprojectM.so.4` is loaded by `JukeboxVisualizations.dll`'s `Native/ProjectMNative.cs` from the same `<appdir>/lib/` folder — flat, alongside every other drop-in file.

---

## 4. Avalonia Controls Project Forks

The Jukebox solution references custom, local forks of the Avalonia DataGrid and TreeDataGrid repositories. These must exist as folders adjacent to the main `Jukebox` project directory:

* **DataGrid Fork:** Expected at `../Avalonia.Controls.DataGrid/`
* **TreeDataGrid Fork:** Expected at `../Avalonia.Controls.TreeDataGrid/`

The solution will fail to build if these custom fork projects are missing. Ensure they are cloned into their respective directories.

---

## Project Links and Downloads

* **BASS (Un4seen Developments):** [https://www.un4seen.com/](https://www.un4seen.com/)
* **libmpv Builds (Windows):** [https://sourceforge.net/projects/mpv-player-windows/files/libmpv/](https://sourceforge.net/projects/mpv-player-windows/files/libmpv/)
* **Jukebox-Visualizations Repository:** [https://github.com/RobG66/Jukebox-Visualizations](https://github.com/RobG66/Jukebox-Visualizations)
* **Avalonia Controls DataGrid Fork:** [https://github.com/RobG66/Avalonia.Controls.DataGrid](https://github.com/RobG66/Avalonia.Controls.DataGrid)
* **Avalonia Controls TreeDataGrid Fork:** [https://github.com/RobG66/Avalonia.Controls.TreeDataGrid](https://github.com/RobG66/Avalonia.Controls.TreeDataGrid)
