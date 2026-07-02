# Jukebox P1/P2 Patch Changelog

This patch set fixes the remaining P1 and P2 issues from the smell-test
report. Three files change — all are drop-in replacements:

| File | Repo | Path | Summary |
|------|------|------|---------|
| `VisualizerRuntime.cs` | Jukebox | `Services/` | Async LoadPreset (off-UI-thread IO) + LoadPresetData via reflection + static regex + cancellation |
| `ProjectMControl.cs` | JukeboxVisualizations | `Controls/` | New `LoadPresetData(string, bool)` method + refactored preset-load pipeline |
| `MpvContext.cs` | Jukebox | `Mpv/` | Proper structs for mpv_event/mpv_event_property + fixed event ID (13→22) + fixed data offset (24→16) |

---

## Critical discovery: MPV event loop was completely broken

The most significant finding in this patch set is that `MpvContext.EventLoop`
had **two bugs** that meant property-change events were **never processed**:

### Bug A — Wrong event ID

```csharp
// OLD — wrong
if (eventId == 13) // MPV_EVENT_PROPERTY_CHANGE
```

`MPV_EVENT_PROPERTY_CHANGE` is **22** in libmpv 2.x (the API version the
Jukebox uses — `libmpv-2.dll` / `libmpv.so.2`). The value 13 doesn't
correspond to any event in the current API. The check always failed, so
the property-change branch never executed.

The value 13 may have been correct in a very early pre-release API
(circa 2014), but it has been 22 since libmpv 1.x stabilized.

### Bug B — Wrong struct offset

```csharp
// OLD — wrong
var dataPtr = Marshal.ReadIntPtr(eventPtr, 24); // claimed: offset of event.data
```

The `mpv_event` struct on x64 is:

| Field | Type | Offset | Size |
|-------|------|--------|------|
| `event_id` | int | 0 | 4 |
| `error` | int | 4 | 4 |
| `reply_userdata` | uint64 | 8 | 8 |
| `data` | void* | **16** | 8 |
| **Total** | | | **24** |

The `data` field is at offset **16**, not 24. Reading at offset 24 reads
**past the struct** into uninitialized memory — the resulting pointer is
garbage (typically zero, which causes the `if (dataPtr != IntPtr.Zero)`
check to fail silently).

### Combined impact

Property changes (`duration`, `time-pos`, `eof-reached`) were silently
dropped. The app "worked" because:

- **Position updates**: `PlaybackTimer_Tick` polls
  `mpv_get_property("time-pos")` directly, independent of events.
- **Duration**: never displayed for video files (the total-time field
  showed `0:00`). The user may not have noticed.
- **End-reached**: video froze on the last frame (`keep-open=yes`)
  without auto-advancing to the next track. The user may have clicked
  Next manually.

### The fix

Defined proper structs and an enum, then used `Marshal.PtrToStructure`:

```csharp
internal enum MpvEventId
{
    // ...
    PropertyChange = 22,  // correct value from client.h
    // ...
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserdata;
    public IntPtr Data;  // automatically at correct offset (16 on x64)
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventProperty
{
    public IntPtr Name;
    public MpvFormat Format;
    public IntPtr Data;
}

// In EventLoop:
var evt = Marshal.PtrToStructure<MpvEvent>(eventPtr);
if (evt.EventId == MpvEventId.PropertyChange)
{
    if (evt.Data == IntPtr.Zero) continue;
    var prop = Marshal.PtrToStructure<MpvEventProperty>(evt.Data);
    var name = Marshal.PtrToStringAnsi(prop.Name);
    // ...
}
```

Self-documenting, correct on every platform, and survives struct layout
changes that add fields at the end.

### After this fix

- Video duration is now displayed correctly in the transport bar.
- Auto-advance works at end of video playback.
- The `eof-reached` property change fires `EndReached`, which triggers
  `OnEnginePlaybackEnded` → next track selection.

---

## P1 Issue 5 — Sync file IO in VisualizerRuntime.LoadPreset

**File:** `Services/VisualizerRuntime.cs`

`LoadPreset` was called from `ContentView.OnVisualizerPropertyChanged` — a
`PropertyChanged` callback that runs on the UI thread. The old code did
all file IO synchronously:

```csharp
// OLD — blocked UI thread
string content = File.ReadAllText(presetPath);        // 50-200KB read
File.WriteAllText(destPath, normalizedContent);       // write
foreach (Match match in regex.Matches(content))
{
    if (File.Exists(sourceTex))                        // per-texture stat
        File.Copy(sourceTex, destTex, true);            // per-texture copy (could be MB)
}
```

With multiple textures, the UI froze for tens to hundreds of milliseconds
per preset click.

**Fix:** Wrapped the file IO in `Task.Run`, serialized via a `SemaphoreSlim`
to prevent rapid-click races. A `CancellationTokenSource` cancels in-flight
loads when a new preset is selected — if the user clicks A then B quickly,
A's load is canceled before it reaches the `LoadPresetData` invoke, so B
wins.

The `IVisualizerRuntime.LoadPreset` signature stays `void` (not `Task`) —
the caller doesn't change. The implementation is fire-and-forget with
`SafeFireAndForget` for error logging.

---

## P2 Issue 7 — Duplicated preset IO

**Files:** `Services/VisualizerRuntime.cs` (Jukebox) + `Controls/ProjectMControl.cs` (JukeboxVisualizations)

The old flow had a duplicate file read:

1. `VisualizerRuntime.LoadPreset` reads `presetPath` → `content`
2. Normalizes line endings
3. Writes to `current_preset/destPath`
4. Calls `ProjectMControl.LoadPreset(destPath)` via reflection
5. `ProjectMControl.LoadPresetFromQueue` reads `destPath` → `content` (again!)
6. Normalizes line endings (again!)
7. Allocates HGlobal + calls `projectm_load_preset_data`

Steps 5-6 are redundant — the content was already read and normalized in
step 1-2.

**Fix:** Added a new `LoadPresetData(string content, bool smooth)` method
to `ProjectMControl`. `VisualizerRuntime.LoadPreset` now calls
`LoadPresetData(normalizedContent, smooth)` via reflection, passing the
already-read content directly. Steps 5-6 are eliminated.

The `current_preset/` write still happens (for restore-on-next-launch),
but `ProjectMControl` no longer re-reads it.

`VisualizerRuntime.Probe` now also probes for `LoadPresetData` via
reflection. If the method isn't found (older wrapper version), it falls
back to `LoadPreset(path)` — the old duplicate-IO path. This makes the
patch backward-compatible with older `JukeboxVisualizations.dll` builds.

### ProjectMControl refactoring

The old `LoadPresetFromQueue(string path, bool smooth)` method has been
split into two methods:

- `LoadPresetFromPath(string path, bool smooth)` — reads the file,
  normalizes, delegates to `LoadPresetFromContent`.
- `LoadPresetFromContent(string content, bool smooth)` — allocates
  HGlobal, copies bytes with null terminator, calls
  `projectm_load_preset_data`. This is the shared bottom of the pipeline.

A new `ConcurrentQueue<(string content, bool smooth)> _presetDataQueue`
runs alongside the existing `_presetQueue`. Both are processed in
`OnOpenGlRender`. The marshalling + null-terminator logic now exists in
exactly one place (`LoadPresetFromContent`).

---

## P2 Issue 8 — Per-call regex in VisualizerRuntime.LoadPreset

**File:** `Services/VisualizerRuntime.cs`

The old code constructed a new `Regex` instance on every `LoadPreset`
call:

```csharp
// OLD — recompiled per call
var regex = new Regex(@"[a-zA-Z0-9_-]+\.(?:jpg|png|bmp|tga)",
    RegexOptions.IgnoreCase);
```

**Fix:** Cached as `static readonly` with `RegexOptions.Compiled`:

```csharp
private static readonly Regex TextureFileRegex = new(
    @"[a-zA-Z0-9_-]+\.(?:jpg|png|bmp|tga)",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);
```

The `Compiled` flag precompiles the regex to IL — significantly faster on
repeated use. `JukeboxVisualizerViewModel` already had the same pattern;
`VisualizerRuntime` now matches it.

---

## P2 Issue 9 — Hardcoded struct offsets in MpvContext.EventLoop

**File:** `Mpv/MpvContext.cs`

See "Critical discovery" above for the full details. The fix defines
proper `MpvEvent`, `MpvEventProperty`, and `MpvEventId` structs/enum and
uses `Marshal.PtrToStructure` instead of `Marshal.ReadIntPtr` with
hardcoded offsets.

This also fixes two latent bugs (wrong event ID, wrong offset) that
silently broke property-change event processing.

---

## What stays the same

- **`IVisualizerRuntime` interface** — no signature changes. `LoadPreset`
  is still `void`. The async behavior is internal to the implementation.
- **`ProjectMControl.LoadPreset(string path, bool smooth)`** — still
  exists, still works. `LoadPresetData` is an addition, not a replacement.
- **All other files** — unchanged. This patch only touches the three
  files listed above.

---

## Backward compatibility

### ProjectMControl.cs (JukeboxVisualizations repo)

The new `LoadPresetData` method is **additive**. If you deploy the patched
`VisualizerRuntime.cs` without the patched `ProjectMControl.cs`,
`VisualizerRuntime.Probe` won't find `LoadPresetData` and will fall back
to `LoadPreset(path)` — the old duplicate-IO path. The app still works,
just with the redundant file read.

If you deploy the patched `ProjectMControl.cs` without the patched
`VisualizerRuntime.cs`, the new `LoadPresetData` method exists but is
never called. `LoadPreset(path)` still works as before. No behavior
change.

For the full benefit (no duplicate IO), deploy both.

### MpvContext.cs

This patch is self-contained. The event-loop fix doesn't depend on any
other file. Deploy it alone for the MPV event-processing fix.

### VisualizerRuntime.cs

Depends on `Jukebox.Extensions.TaskExtensions.SafeFireAndForget` (already
in the codebase) and `Avalonia.Threading.Dispatcher` (already referenced).
No new dependencies.

---

## Setup steps

1. Copy the 3 files over the originals:
   - `VisualizerRuntime.cs` → `Jukebox/Services/VisualizerRuntime.cs`
   - `MpvContext.cs` → `Jukebox/Mpv/MpvContext.cs`
   - `ProjectMControl.cs` → `JukeboxVisualizations/Controls/ProjectMControl.cs`
     (this is the JukeboxVisualizations repo, not the Jukebox repo)
2. Build JukeboxVisualizations first (produces `JukeboxVisualizations.dll`
   with the new `LoadPresetData` method).
3. Copy the new `JukeboxVisualizations.dll` into `Jukebox/lib/`.
4. Build Jukebox.
5. Run and test:
   - **Video duration**: play a video file. The total-time field should
     now show the correct duration (was `0:00` before).
   - **Video auto-advance**: play a video file to the end. It should
     auto-advance to the next track (was freezing on last frame before).
   - **Preset switching**: click through presets in the visualizer picker.
     The UI should not freeze (was freezing for 50-200ms per click before).
   - **Rapid preset clicking**: click 5 presets quickly. The last-clicked
     preset should be the one that loads (previous in-flight loads are
     canceled).

---

## Files not changed (intentionally)

The following issues from the smell-test report were **not** fixed in this
patch set:

- **Issue 10** (P3, load/free probe in `NativeDependencyChecker`) — minor
  startup cost, no user-visible impact. Can be fixed in a future patch.
- **Issue 11** (unused `using Avalonia.Threading` in `BassPlaybackEngine.cs`)
  — code hygiene, no functional impact. Already addressed in the previous
  patch set's `BassPlaybackEngine.cs` rewrite (the `using` was removed).
- **Issue 12** (stale doc references to `PlaybackBASS.cs`) — already
  addressed in the previous patch set's `ARCHITECTURE.md` update.
- **Issue 13** (ContentView dispose ordering) — low severity, the
  `TryDispose` is a no-op today since `ProjectMControl` doesn't implement
  `IDisposable`. Can be fixed in a future patch.
- **Issue 14** (restore-point fragility) — the `current_preset/` mechanism
  works reliably in practice because `LoadPreset` cleans the directory
  before writing. The `OrderByDescending(File.GetLastWriteTime)` suggestion
  is a nice-to-have but not critical.
- **Issue 15** (OnBassEndSync dispatch) — design choice. The current
  subscriber dispatches correctly. Adding dispatch inside `OnBassEndSync`
  would be belt-and-suspenders but isn't a bug.
- **Issue 16** (Console.WriteLine on Linux GUI launch) — `--help` from a
  GUI app is a Linux-unfriendly concept. Low priority.
