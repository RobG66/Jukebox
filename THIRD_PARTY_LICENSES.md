# Third-Party Licenses

This document lists the third-party libraries, native binaries, and NuGet
packages used by the Jukebox project, along with their licenses and where
to obtain the source. It exists to make licensing obligations explicit
and to ensure compliance with permissive, copyleft, and proprietary
license terms alike.

For the avoidance of doubt: **Jukebox does not commit third-party native
binaries (`.dll` / `.so` / `.dylib`) to this repository.** They are
fetched at build time via the `fetch-natives.ps1` / `fetch-natives.sh`
script, which downloads them from upstream sources or a project-managed
GitHub release. See [DEPENDENCIES.md](DEPENDENCIES.md) for details.

---

## Native runtime libraries (dropped into `lib/`)

These are unmanaged native binaries loaded at runtime via P/Invoke or
`NativeLibrary.Load`. They are NOT committed to the repo — see
`fetch-natives.ps1` / `fetch-natives.sh`.

### BASS Audio Library (`bass.dll` / `libbass.so`)

* **Website:** https://www.un4seen.com/
* **License:** Proprietary — free for non-commercial use; commercial use
  requires a paid license from Un4seen Developments.
* **License terms:** https://www.un4seen.com/#license
* **Source availability:** Closed source.
* **Redistribution:** Allowed for non-commercial apps, provided:
  1. The `bass.dll` / `libbass.so` file is not modified.
  2. The BASS license file is included in the distribution.
  3. The copyright notice is preserved.

  Jukebox is currently a non-commercial project. If Jukebox ever gains
  commercial use (paid tier, ads, etc.), a BASS commercial license must
  be purchased from Un4seen.

### libmpv (`libmpv-2.dll` / `libmpv.so.2`)

* **Website:** https://github.com/mpv-player/mpv
* **License:** **GPL v2+** by default. LGPL v2.1+ if built with
  `--enable-lgpl`.
* **License file:** https://github.com/mpv-player/mpv/blob/master/COPYING.GPL
* **Source availability:** Open source.
* **Redistribution concerns:**

  > ⚠️ **GPL notice.** The default libmpv build is GPL-licensed.
  > Dynamically linking a non-GPL application against a GPL library
  > creates a combined derivative work that, under the FSF's
  > interpretation, must be distributed under GPL. The FSF's position
  > is that dynamic linking does not avoid this obligation.
  >
  > If Jukebox is not GPL-licensed, you should either:
  > (a) build libmpv with `--enable-lgpl` to produce an LGPL build
  >     (which permits dynamic linking from non-GPL apps), OR
  > (b) require users to install libmpv themselves (e.g. via
  >     `apt install libmpv-dev` on Linux), so the redistribution
  >     obligation falls on them, OR
  > (c) license Jukebox itself under GPL v2+.
  >
  > Most pre-built libmpv binaries available for download (SourceForge
  > Windows builds, distro packages) are GPL builds, not LGPL.

  The `fetch-natives` script downloads libmpv from upstream. The user
  accepts the GPL terms by running the script. Jukebox itself does not
  redistribute libmpv in source-controlled history.

### GLEW (OpenGL Extension Wrangler Library) — `glew32.dll`

* **Website:** https://glew.sourceforge.net/
* **License:** BSD 3-Clause + MIT (dual licensed).
* **License file:** https://github.com/nigels-com/glew/blob/master/LICENSE.txt
* **Source availability:** Open source.
* **Redistribution:** Allowed, including in binary form, provided the
  copyright notice and license are included. No copyleft obligation.

  Used by `libprojectM.dll` on Windows. Bundled with the libprojectM
  release artifact produced by the Jukebox-Visualizations CI workflow.

---

## NuGet packages (managed assemblies)

These are .NET assemblies pulled in via `<PackageReference>` in
`Jukebox.csproj`. They ship with their own license files in the NuGet
package itself.

### Avalonia UI (`Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter`)

* **Website:** https://avaloniaui.net/
* **License:** MIT License.
* **Source:** https://github.com/AvaloniaUI/Avalonia

### ManagedBass (`ManagedBass`)

* **Website:** https://github.com/ManagedBass/ManagedBass
* **License:** MIT License.
* **Source:** https://github.com/ManagedBass/ManagedBass
* **Note:** ManagedBass is a managed wrapper around the native BASS
  library. The native `bass.dll` / `libbass.so` is governed by the
  proprietary BASS license (see above), not by ManagedBass's MIT
  license.

### TagLibSharp (`TagLibSharp`)

* **Website:** https://github.com/mono/taglib-sharp
* **License:** LGPL v2.1.
* **Source:** https://github.com/mono/taglib-sharp

### CommunityToolkit.Mvvm (`CommunityToolkit.Mvvm`)

* **Website:** https://github.com/CommunityToolkit/dotnet
* **License:** MIT License.
* **Source:** https://github.com/CommunityToolkit/dotnet

### AvaloniaUI.DiagnosticsSupport

* **Website:** https://github.com/AvaloniaUI/Avalonia
* **License:** MIT License.

---

## Local project forks (managed assemblies)

These are .NET projects cloned into sibling directories and referenced
via `<ProjectReference>`. They are not NuGet packages.

### Avalonia.Controls.DataGrid (custom fork)

* **Source:** https://github.com/RobG66/Avalonia.Controls.DataGrid
* **License:** MIT License (inherited from upstream Avalonia).

### Avalonia.Controls.TreeDataGrid (custom fork)

* **Source:** https://github.com/RobG66/Avalonia.Controls.TreeDataGrid
* **License:** MIT License (inherited from upstream Avalonia).

---

## Optional companion project — Jukebox-Visualizations

* **Source:** https://github.com/RobG66/Jukebox-Visualizations
* **License:** See that project's `THIRD_PARTY_LICENSES.md` for the
  licensing of `JukeboxVisualizations.dll`, `libprojectM`, and GLEW.
* **Distribution model:** The compiled `JukeboxVisualizations.dll` is
  loaded at runtime via reflection (`Services/VisualizerRuntime.cs`)
  from the `lib/` folder. It is not committed to this repo.

---

## Updating this file

When adding a new third-party dependency (native binary or NuGet
package), add an entry to the appropriate section above. Include:

1. Name and version (if pinned).
2. Website or source URL.
3. License name and link to the license text.
4. Whether source is available.
5. Any redistribution obligations or caveats.

When in doubt, prefer over-disclosure. This file is part of the legal
record of what the project ships.
