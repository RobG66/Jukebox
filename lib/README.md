# Native runtime libraries AND the optional JukeboxVisualizations.dll
# managed wrapper go here, flat — no subfolders.
#
# This folder is intentionally empty in the repository. It is populated
# by the fetch-natives script, which downloads the third-party native
# binaries from the URLs listed in natives.json, verifies their SHA-256
# checksums, and extracts them here.
#
# ── Quick start ────────────────────────────────────────────────────
# Run one of these from the project root:
#
#   Windows:  .\fetch-natives.ps1
#   Linux:    ./fetch-natives.sh
#
# The script is idempotent — safe to re-run. Pass -Force / --force to
# re-download everything.
#
# ── What lives here after fetch-natives runs ───────────────────────
#
#   Windows:
#     bass.dll              — BASS audio library (proprietary, non-commercial)
#     libmpv-2.dll          — libmpv video library (GPL v2+ or LGPL v2.1+)
#     JukeboxVisualizations.dll   — managed wrapper (from Jukebox-Visualizations repo)
#     libprojectM.dll       — ProjectM visualizer engine (LGPL v2.1+) [optional]
#     glew32.dll            — GLEW, required by libprojectM.dll (BSD/MIT) [optional]
#
#   Linux:
#     libbass.so            — BASS audio library (proprietary, non-commercial)
#     libmpv.so.2           — libmpv video library (GPL v2+ or LGPL v2.1+)
#     JukeboxVisualizations.dll   — managed wrapper (same DLL for all platforms)
#     libprojectM.so.4      — ProjectM visualizer engine (LGPL v2.1+) [optional]
#
# Windows .dll and Linux .so files coexist in this same folder; the
# loader code picks the right filename per OS at runtime.
#
# ── Why we don't commit these to git ───────────────────────────────
# See THIRD_PARTY_LICENSES.md — third-party binaries carry licensing
# obligations (GPL/LGPL/proprietary) that we don't want to entangle
# with this repo's history. The fetch-natives script is the chain of
# trust: URL in natives.json -> SHA-256 match -> lib/.
#
# ── Updating a library ─────────────────────────────────────────────
# Edit natives.json: bump the URL + sha256 to the new version, commit
# the change, and re-run fetch-natives. Old binaries are never in git
# history.
#
# See DEPENDENCIES.md for the full runtime directory layout.
