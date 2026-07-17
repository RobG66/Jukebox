# lib/ — native runtime libraries

All third-party native runtime libraries go in this folder, flat —
no subfolders. Windows `.dll` and Linux `.so` files coexist by
extension; the Jukebox loader code picks the right filename per OS
at runtime.

This folder is intentionally empty in the repository. You must
populate it manually before running Jukebox. The Jukebox checks at
startup and will show a clear error dialog listing what's missing.

---

## Required libraries (audio + video — always needed)

### Windows
| File | Source | License |
|------|--------|---------|
| `bass.dll` | https://www.un4seen.com/ (download `bass24.zip`, 64-bit) | Proprietary, non-commercial |
| `libmpv-2.dll` | https://sourceforge.net/projects/mpv-player-windows/files/libmpv/ (download latest `mpv-dev-x86_64-*.7z`, extract with 7-Zip, find `libmpv-2.dll` inside) | GPL v2+ (or LGPL if built with `--enable-lgpl`) |

### Linux
| File | Source | License |
|------|--------|---------|
| `libbass.so` | https://www.un4seen.com/ (download `bass24-linux.zip`) | Proprietary, non-commercial |
| `libmpv.so.2` | `sudo apt install libmpv-dev` (places it in `/usr/lib/x86_64-linux-gnu/`), OR download from https://github.com/mpv-player/mpv releases | GPL v2+ (or LGPL if built with `--enable-lgpl`) |

> **Linux alternative:** if you install `libmpv-dev` via apt, the Jukebox
> will find `libmpv.so.2` on the system library path even if it's not in
> `lib/`. The loader falls back to the OS default search path.

---

## Optional ProjectM visualizer

ProjectM does not load from `lib/`. Keep its managed wrapper, native
libraries, license, presets, and textures together under
`plugins/Avalonia.ProjectM/`. See [../DEPENDENCIES.md](../DEPENDENCIES.md)
for the authoritative package layout.

---

## What the final layout looks like

After populating `lib/` and installing the optional ProjectM plugin, your
Jukebox build output directory should look like:

```
<appdir>/
├── Jukebox.exe
├── lib/                               ← host playback runtimes, flat
│   ├── bass.dll                       (Windows — BASS audio)
│   ├── libbass.so                     (Linux   — BASS audio)
│   ├── libmpv-2.dll                   (Windows — libmpv video)
│   └── libmpv.so.2                    (Linux   — libmpv video)
└── plugins/
    └── Avalonia.ProjectM/             (optional, self-contained)
        ├── Avalonia.ProjectM.dll
        ├── libprojectM.dll            (Windows)
        ├── glew32.dll                 (Windows)
        ├── libprojectM.so.4           (Linux)
        └── ProjectM/
            ├── presets/
            ├── textures/
            └── current_preset/
```

---

## Licensing notes

See [../THIRD_PARTY_LICENSES.md](../THIRD_PARTY_LICENSES.md) for the
full licensing breakdown. Key points:

- **BASS** is proprietary — free for non-commercial use only.
- **libmpv** default builds are GPL v2+. If you redistribute Jukebox
  with libmpv bundled, ensure your licensing accommodates this (or use
  an LGPL build).
- **libprojectM** is LGPL v2.1+ — dynamic linking is fine, but
  `libprojectM-LICENSE.txt` must accompany the binary in the plugin package.
- **GLEW** is BSD/MIT — no real restrictions.

---

## Why we don't commit these to git

Third-party binaries carry licensing obligations (GPL/LGPL/proprietary)
that we don't want entangled with the repo's history. Manual placement
makes the obligations explicit: you accept each library's license by
downloading it yourself.

See [../DEPENDENCIES.md](../DEPENDENCIES.md) for more details.
