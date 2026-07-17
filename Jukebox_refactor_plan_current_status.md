# Jukebox Refactor Plan — Current Status and Remaining Work

**Updated:** 2026-07-16  
**Repository:** `E:\source\repos\RobG66\Jukebox`  
**Authority:** This file supersedes the earlier copy in `Downloads`. The current local source tree is the implementation baseline.

---

## 1. Fixed project decisions

### One global runtime Play Queue

- `PlayQueue` is the only collection used for playback navigation.
- Previous, Next, natural advancement, repeat, loop, and shuffle operate only on `PlayQueue`.
- Saved playlists are persisted collections and never control active playback directly.
- Plugins may replace, append to, or insert into the host queue, but must not own a second runtime playback playlist.

### Saved playlists use copy semantics

- Loading or playing a saved playlist copies tracks into `PlayQueue`.
- Editing, clearing, switching, or deleting a saved playlist must not mutate or stop active queue playback.
- Saving the queue creates a persisted copy; it does not create a live link.
- Adding tracks to another saved playlist copies them and never removes them from the source.

### Cross-platform desktop UI

- The application remains an Avalonia desktop GUI for Windows and Linux.
- Controls must use logical pixels and remain usable at normal desktop scaling.
- Avoid platform-only fonts, operating-system symbol fonts, online icon services, and hover-only essential actions.
- A persistent 64-pixel media navigation rail owns Queue, Saved Playlists, and plugin destinations.
- Queue and Saved Playlists use the compact 430-pixel host panel.
- Plugin browsers use the full remaining content surface and never open separate browser windows.

### Updated icon decision

The earlier PNG-only restriction was explicitly relaxed by the user.

- Bundled vector geometry is now preferred for standard actions and navigation icons.
- Vectors must be local, themeable, DPI-safe, and render identically on Windows and Linux.
- Plugins may still supply bitmap icons when they represent useful source branding.
- Do not use emoji, Unicode glyphs as icons, textual stand-ins, or unbundled/platform-specific icon fonts.

### Protected local scripts

Never modify, replace, or delete:

```text
build.sh
build.ps1
tag-release.ps1
```

### Final ProjectM package identity

```text
Plugin:    Avalonia.ProjectM
Assembly:  Avalonia.ProjectM.dll
Directory: plugins/Avalonia.ProjectM/
```

Do not restore the obsolete `ProjectM.Avalonia` name or an application-level `lib/` fallback.

Required native files:

- Windows: `libprojectM.dll`, `glew32.dll`
- Linux: `libprojectM.so.4`
- macOS when supported: `libprojectM.dylib`

---

## 2. Current verification baseline

The current solution was built repeatedly after Stages 4–7 and the media-drawer work:

```text
dotnet build Jukebox.slnx --no-restore --nologo
Build succeeded.
0 Warning(s)
0 Error(s)
```

Hidden startup checks confirmed:

- all three media-browser plugins load;
- all three direct plugin browser destinations are created;
- `Avalonia.ProjectM.dll` is discovered from its final package directory;
- the ProjectM native OpenGL hook initializes;
- the application has no startup XAML or command-generation exception.

These automated checks do not replace the manual playback and interaction checks listed later.

---

## 3. Completed work

## Stage 1 — Queue foundation

**Status: Implemented and compiling**

- Added the non-null global `PlayQueue` collection.
- Added stable online-source metadata to requests, tracks, and saved DTOs.
- Centralized `PlayRequest` to `JukeboxTrack` mapping.
- Routed plugin play/add operations into the host queue.
- Added queue replace, append, clear, remove, URL-update, and track-copy APIs.
- Removed the temporary `ActivePlaylist` compatibility alias after playback migration.

## Stage 2 — Playback migration

**Status: Implemented and compiling; manual playback matrix still required**

- Playback navigation now uses only `PlayQueue`.
- Migrated Play, Previous, Next, shuffle, repeat, loop, and end-of-track advancement.
- Added explicit queue replacement, clearing, and playing-row-removal events.
- Added resolver cancellation and playback-generation protection.
- Detached native engine completion handlers before manual stops.
- Removed a duplicate shuffle-history reset path.
- Added stable KHInsider source URLs and host resolver support.

## Stage 3 — Queue and Saved Playlists UI split

**Status: Implemented, compiling, and startup verified**

- Added separate `PlayQueueView` and `SavedPlaylistsView` pages.
- Kept Queue and Saved Playlists as permanent tabs.
- Continued dynamic plugin-tab hosting.
- Added queue save/remove/clear controls.
- Added saved-playlist selector, create/delete, file/folder import, remove, clear, and search controls.
- Added persistent playback-mode controls in the drawer footer.
- Split double-click behavior between queue rows and saved rows.
- Preserved the blank `Default` playlist fallback.

## Stage 4 — Baseline synchronization and stabilization

**Status: Complete**

- Reconciled work directly in the user's current local source tree.
- Repaired and verified compiled Avalonia bindings.
- Fixed duplicate shuffle-history clearing.
- Confirmed clean full-solution builds and startup.
- Verified plugin discovery and basic ProjectM runtime initialization.

## Stage 5 — Avalonia.ProjectM naming and discovery cleanup

**Status: Complete**

- Discovery probes only `plugins/Avalonia.ProjectM/Avalonia.ProjectM.dll`.
- Verified the exported control type as `Avalonia.ProjectM.Controls.ProjectMControl`.
- Windows availability requires both `libprojectM.dll` and `glew32.dll`.
- Linux availability requires `libprojectM.so.4`.
- Presets are required under `plugins/Avalonia.ProjectM/ProjectM/presets`.
- Removed application-level `lib/` fallback behavior for ProjectM.
- Generic media-browser plugin discovery skips the visualizer assembly.
- Removed old app-level ProjectM native copies and obsolete naming references.

## Media shell and embedded browser pass

**Status: Implemented, compiling, and visually approved for continued use**

- Added shared media-panel styles and theme resources.
- Made the drawer substantially more opaque and readable over visualizations.
- Reduced translucent nested-card styling and tightened desktop spacing.
- Replaced standard PNG action icons with bundled themeable vectors.
- Added a persistent 64-pixel navigation rail.
- Queue and Saved Playlists retain a compact 430-pixel host panel.
- Plugin browsers fill the remaining application content area.
- Queue and Saved Playlists use the bundled `music.png` and `folder.png` assets.
- Retained third-party/plugin bitmap fallback for source branding.
- Plugins supply their own navigation bitmap or vector path data; the host controls sizing and selection treatment.
- Left the visualizer picker panel unchanged.
- Added Escape navigation: browser → last host panel → closed media surface.

Important: `Constants.SidePanelWidth` remains `430` logical pixels for compact host panels; it no longer constrains plugin browser content.

## Stage 6 — Queue and Saved Playlist interactions

**Status: Implemented and compiling; manual interaction checks required**

- Added saved-row **Play now** using a one-track queue copy.
- Preserved **Play this playlist** behavior for whole-playlist queue replacement.
- Added multi-selection **Queue next**.
- Added multi-selection **Queue last**.
- Added dynamic **Add to playlist** targets using copy semantics.
- Added Queue and Saved Playlist desktop context menus.
- Added scoped keyboard behavior:
  - `Enter`: normal row-play behavior;
  - `Ctrl+Enter`: play one saved row now;
  - `Ctrl+Shift+N`: queue selected tracks next;
  - `Ctrl+Shift+End`: queue selected tracks last;
  - existing Delete, clear, save, and import shortcuts remain scoped to their views.
- Added null-safe keyboard execution when no row is selected.
- Audited saved-list mutation paths for auto-save behavior.

## Stage 7 — Explicit import targets and routing

**Status: Implemented and compiling; manual drag/drop checks required**

- Added explicit import targets:
  - `PlaylistTarget.PlayQueue`
  - `PlaylistTarget.SelectedSavedPlaylist`
- `ProcessAndAddFilesAsync` now requires a destination and returns the exact tracks added.
- Removed fragile destination/index assumptions from import callers.
- Drag/drop routing now follows the visible page:
  - visible Saved Playlists page → selected saved playlist and auto-save;
  - Queue page → Play Queue;
  - plugin page → Play Queue;
  - closed drawer → Play Queue.
- Saved-page drops never start or interrupt playback.
- Queue/plugin drops may start the first imported queue item only when idle.
- Command-line `-file` startup imports into `PlayQueue`.
- Embedded/API media loading defaults to `PlayQueue`.
- Added an explicit embedded-host overload for callers that intentionally target the selected saved playlist.
- Queue imports receive asynchronous metadata tagging without touching saved collections.
- No tab-index magic was added; routing uses the centralized `ActiveTab` mapping.

## Stages 8–10 — Direct embedded plugin browsers

**Status: Implemented and compiling; manual network/playback checks required**

- Radio, KHInsider, and Internet Archive now expose their direct browser views to the host.
- Removed plugin-owned playlist wrapper views, view-models, persistence, messaging bridges, and popup compatibility paths.
- Removed plugin-owned save-on-close prompts and `Playlists` directory creation.
- Radio Play replaces the host queue with the station; Add appends to the host queue.
- KHInsider Play replaces the host queue with the whole album and starts the selected track.
- KHInsider selected/all Add operations append directly to the host queue.
- KHInsider preserves stable track-page URLs and resolves transient CDN URLs in the background.
- Internet Archive Play replaces the host queue with the whole album and starts the selected track.
- Internet Archive selected/all Add operations append directly to the host queue.
- Plugin context operations are now named explicitly for host queue ownership:
  - `ReplaceQueueAndPlay`
  - `AddToQueue`
  - `AddRangeToQueue`

## Stage 11 — Plugin lifecycle and ownership

**Status: Implemented and compiling; shutdown cancellation should receive manual stress testing**

- The host owns each plugin browser instance and created view.
- `IJukeboxMediaBrowser.Dispose()` provides a source-compatible lifecycle hook.
- The Jukebox invokes every browser's disposal hook exactly once during shutdown.
- Direct browser view-models detach playback subscriptions.
- KHInsider cancels and disposes active resolution/search work.
- Internet Archive cancels and disposes active search and album-loading work.
- Obsolete messenger registrations were removed with the wrapper layers.

---

## 4. Known issues and deferred decisions

### Media sizing is resolved for the current design

- The persistent navigation rail is 64 logical pixels.
- Queue and Saved Playlists retain the 430-pixel compact host panel.
- Plugin browser content fills all remaining width.
- No resizable plugin drawer is required because browsers are no longer constrained by the host panel.
- The visualizer picker remains a separate 430-pixel panel.

### ProjectM saved-preset warning

ProjectM loads and initializes, but later forced startup checks logged:

```text
Preset file not found: .../ProjectM/current_preset/186.milk
```

An earlier runtime check successfully loaded the same preset from a normal preset directory. Treat the `current_preset` reference as a separate persisted-state/path issue to investigate during future visualizer work. It did not cause an application startup exception.

### Documentation baseline

The root README, application README, architecture guide, embedding guide, dependency layout, and Internet Archive plugin README now describe the host-owned queue and embedded-browser model.

### Manual runtime verification remains

The workspace contains no small media fixture, so real-file import and playback routing were not automated. Perform the manual matrix below before declaring Stages 1–7 fully runtime-verified.

---

## 5. Remaining implementation order

## Runtime verification pass

**Status: Required before compatibility cleanup is declared final**

1. Complete the playback and queue-isolation matrix.
2. Verify direct Radio, KHInsider, and Internet Archive play/add operations against live services.
3. Verify shutdown while plugin searches and KHInsider resolution are active.
4. Verify Windows high-DPI and Linux layout behavior.

## Stage 12 — Compatibility cleanup and documentation

**Status: Implemented and compiling; additional documentation can evolve with future features**

- Removed the `ActivePlaylist` alias.
- Removed unused transient-radio queue state and filtering.
- Removed the unused app-level radio-cache path.
- Removed the unrendered playlist-logo property and command-line switch.
- Removed the redundant `JUKEBOX / Media` host-panel header.
- Updated importer examples for explicit `PlaylistTarget` routing.
- Updated architecture text to describe one global Play Queue and copy-only saved collections.
- Updated:

```text
Jukebox/ARCHITECTURE.md
Jukebox/DEPENDENCIES.md
Jukebox/EMBEDDING.md
README.md
Jukebox/THIRD_PARTY_LICENSES.md
```

## Deferred visualizer-picker UI pass

**Status: Deferred by user**

- Revisit the visualization picker after the media drawer and plugin browser work.
- Keep this separate from the ProjectM package/discovery cleanup, which is already complete.
- Include the stale `current_preset` path investigation in that later work.

---

## 6. Manual verification matrix

### Playback and queue isolation

- Play at least two local audio tracks through BASS.
- Verify natural end advances exactly once.
- Verify manual Stop does not advance.
- Verify rapid track changes ignore stale callbacks/resolvers.
- Verify queue replace and clear reset shuffle history exactly once.
- Verify queue append/insert preserves valid shuffle history.
- Edit, clear, switch, and delete saved playlists while queue playback continues.

### Stage 6 interactions

- Saved **Play now** creates a one-track queue and starts it.
- Saved **Play this playlist** copies the whole saved list and starts the selected row.
- Multi-select **Queue next** inserts after the current queue row.
- With nothing playing, **Queue next** inserts at the beginning.
- **Queue last** appends in saved-playlist order.
- **Add to playlist** copies to the selected target and persists after restart.
- Adding to the current saved playlist duplicates by copy without selection/enumeration failure.
- Context menus and keyboard shortcuts operate only on their intended page.

### Stage 7 routing

- Drop files/folders on Queue → queue only.
- Drop files/folders on Saved Playlists → selected saved collection and auto-save.
- Drop files/folders on each plugin page → queue only.
- Drop with the drawer closed → queue only.
- Saved-page drops do not start or stop playback.
- `-file` startup enters the queue and plays the imported item.
- Embedded `PlayMediaFilesAsync` defaults to queue.
- Explicit embedded saved target edits only the selected saved playlist.

### Plugins and stable URLs

- Radio play/add operations use the host queue.
- KHInsider host-saved items survive restart and re-resolve from stable source URLs.
- Internet Archive play/add operations use the host queue.
- No plugin owns or mutates a private runtime playlist.

### Cross-platform UI

- Verify Windows and Linux at normal and high-DPI scaling.
- Verify Queue and Saved pages at 430 logical pixels and browsers in the full remaining surface.
- Verify no horizontal overflow.
- Verify vector icons inherit light/dark theme colors.
- Verify plugin bitmap fallback remains usable for third-party branding.

---

## 7. Next-session handoff

Resume from the current local tree and this plan. Do not restore the old PNG-only rule or obsolete ProjectM name. Do not modify the four protected scripts.

Next, complete the runtime verification pass. Keep ProjectM picker work deferred unless the user explicitly resumes it.
