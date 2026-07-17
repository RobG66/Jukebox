# Jukebox

<img width="1605" height="1275" alt="image" src="https://github.com/user-attachments/assets/86f533c9-7dc4-496a-8ddd-c9758621e319" />

A simple, capable cross-platform media and radio player. Built with Avalonia UI.

Jukebox is designed to be straightforward — play your music, watch videos, listen to online radio, and enjoy visualizations, all in one clean interface. It also works as an embeddable component for developers who want to add media playback to their own applications.

**Note: Jukebox is a work in progress.  Documentation may be incomplete, incorrect or missing.**

## Features

- **Audio Playback** — Plays standard formats (MP3, WAV, OGG), lossless music (FLAC - Free Lossless Audio Codec, providing high-fidelity, CD-quality audio), compressed formats (AAC/M4A, WMA, and OPUS—a modern, open-source audio codec designed for highly efficient streaming and playback), and retro chiptunes (VGM, VGZ, VGX) via emulation.
- **Video Playback** — Plays MP4, MKV, AVI, and WEBM formats.
- **ZIP Archives** — Play audio files directly from `.zip` archives without extraction.
- **Online Radio** — Search and stream thousands of global radio stations.
- **Equalizer** — 10-band equalizer with saved presets.
- **Visualizations** — Optional music visualizations with thousands of presets.
- **Drag & Drop** — Drop files or folders onto the window to add them to your playlist.

## Installation

### Windows

Download the latest Windows release from the [Releases](../../releases) page and extract it to a folder of your choice. Run `Jukebox.exe`.

### Linux

A Linux package is coming soon. For now, Linux users can build from source — see [DEPENDENCIES.md](DEPENDENCIES.md).

### Visualizations (Optional)

Visualizations are not included by default. If you want them:

1. Download the `ProjectM.zip` file from the [Releases](../../releases) page
2. Extract its contents into the same folder as Jukebox
3. The visualizer button will appear in the transport bar automatically

You can add or remove visualizations at any time — just restart Jukebox after copying or removing the files.

## Command-Line Switches

All switches are case-insensitive.

| Switch | Value | Description |
|--------|-------|-------------|
| `-light` | — | Use light theme on startup |
| `-dark` | — | Use dark theme on startup |
| `-playlistlogo` | `[file path]` | Show an image logo above the playlist |
| `-random` | — | Shuffle playback |
| `-hidecontrols` | — | Auto-hide the bottom control bar when inactive |
| `-nocontrols` | — | Hide all control panels and keyboard shortcuts |
| `-novisualizer` | — | Turn off visualizations even if available |
| `-showplaying` | `[seconds]?` | Show a "Now Playing" banner when tracks change |
| `-randompreset` | `[seconds]?` | Auto-cycle visualizer presets (10-60 second interval) |
| `-volume` | `[0-100]` | Set startup volume |
| `-stayontop` | — | Keep window on top of other windows |
| `-fullscreen` | — | Start in fullscreen |
| `-minimized` | — | Start minimized |
| `-file` | `[path]` | Open a file or folder on startup |
| `-loop` | — | Loop the playlist continuously |
| `-title` | `[text]` | Set a custom window title |
| `-?` | — | Show help |

### Examples

```bash
# Dark theme, volume 50, open a music folder, loop
Jukebox.exe -dark -volume 50 -file "D:\Music\Playlist" -loop

# Light theme, shuffle, always on top, custom title
Jukebox.exe -light -random -stayontop -title "Retro Jukebox"
```

## Playlists

Jukebox has two playlist tabs: **Library** (your files) and **Radio** (online streams).

- **Drag and drop** files or folders onto the window to add them to your Library playlist. If nothing is playing, the first item starts automatically. If something is already playing, new items are added without interrupting.
- **Save a playlist** by clicking the save icon. Check "Save as default startup playlist" if you want it to load automatically when Jukebox starts.
- If no default playlist is set, Jukebox opens with an empty playlist — just drag in some files and press play.
- Use the playlist dropdown to switch between saved playlists, or create new ones with "Save As".

## Keyboard Shortcuts

| Key | Action |
|-----|--------|
| `Escape` | Close open panels, or exit fullscreen |
| `Space` | Play / Pause |

## Troubleshooting

**Video won't play** — Video support requires native libraries that are included in the release package. If you're building from source, make sure you've set up the `lib/` folder as described in [DEPENDENCIES.md](DEPENDENCIES.md).

**Audio files (FLAC, AAC, OPUS) or online radio streams won't play** — While MP3, OGG, and WAV are handled natively by BASS, advanced formats and HLS stream playback require the respective optional BASS plugins (`bassflac`, `bass_aac`, `bassopus`, `basshls`) to be dropped into the `lib/` folder. If building from source, see [DEPENDENCIES.md](DEPENDENCIES.md) / [lib/README.md](lib/README.md) for details.

**Visualizer button is missing** — The visualizer is optional. Download `ProjectM.zip` from the [Releases](../../releases) page and extract it into your Jukebox folder, then restart.

**Radio won't connect** — Some stations may be offline or have connection issues. Try a different station, or check your internet connection.

## For Developers

Jukebox can be embedded in other Avalonia applications as a user control. If you're a developer looking to integrate Jukebox's media playback capabilities into your own app, see:

- [EMBEDDING.md](EMBEDDING.md) — How to embed Jukebox in your application
- [ARCHITECTURE.md](ARCHITECTURE.md) — Internal architecture and technical details
- [DEPENDENCIES.md](DEPENDENCIES.md) — Native library setup for building from source

## Credits

- [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) — Cross-platform UI framework
- [libmpv](https://github.com/mpv-player/mpv) — Video playback
- [BASS Audio Library](https://www.un4seen.com/) — Audio playback
- [libvgm](https://github.com/RobG66/libvgm) — Video game music emulation
- [projectM](https://github.com/projectM-visualizer/projectm) — Music visualizations
- [Radio Browser API](https://www.radio-browser.info/) — Radio station directory
- [TagLib#](https://github.com/mono/taglib-sharp) — Metadata reading
