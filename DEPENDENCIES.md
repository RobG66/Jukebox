# Jukebox Project Dependencies

This project relies on unmanaged native libraries for audio playback (BASS), video playback (libmpv), and OpenGL visualizations (ProjectM), alongside custom local forks of the Avalonia Controls libraries. These dependencies are not included in the repository and must be setup manually.

---

## 1. ManagedBass (Audio)

The application uses BASS for audio playback and DSP analysis. 

### Windows setup:
1. Download the BASS library from the Un4seen website (see links below).
2. Extract `bass.dll` (64-bit version).
3. Place `bass.dll` in the root of the `Jukebox` project folder (next to `Jukebox.csproj`).

### Linux / macOS setup:
* **Linux:** Download `libbass.so` from Un4seen and install it in your library path (e.g., `/usr/lib/` or `/usr/local/lib/`).
* **macOS:** Download `libbass.dylib` from Un4seen and install it in your library path.

---

## 2. libmpv (Video)

Video rendering is handled via a custom P/Invoke wrapper to `libmpv`.

### Windows setup:
1. Download the `libmpv` Windows build from SourceForge (see links below).
2. Extract the archive and copy `libmpv-2.dll` (on some builds it might be named `mpv-2.dll` — if so, copy it as is, or verify it matches the name `libmpv-2.dll`).
3. Place `libmpv-2.dll` in the root of the `Jukebox` project folder (next to `Jukebox.csproj`).

### Linux / macOS setup:
* **Linux:** Install the development library via your package manager:
  ```bash
  sudo apt install libmpv-dev
  ```
* **macOS:** Install via Homebrew:
  ```bash
  brew install mpv
  ```

---

## 3. ProjectM (Visualizations)

The unmanaged ProjectM visualization library is hosted in the companion repository `Jukebox-Visualizations`.

1. Download or clone the `Jukebox-Visualizations` repository (see links below).
2. Copy the `ProjectM` folder from the root of that project.
3. Paste the `ProjectM` folder into the root of the `Jukebox` project folder.

At runtime, the directory structure in your build output should look like this:
```text
bin/Debug/net8.0/
├── Jukebox.exe
├── bass.dll
├── libmpv-2.dll
└── ProjectM/
    ├── win-x64/
    │   ├── libprojectM.dll
    │   └── glew32.dll
    └── Presets/
        └── (Milkdrop presets)
```

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
