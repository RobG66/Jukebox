# Jukebox

A standalone, cross-platform media player built with C#, Avalonia UI, and LibVLCSharp. 

This application provides a focused, customizable media playback experience designed to be run standalone, in kiosk mode, or integrated seamlessly into other systems via command-line parameters.

## Features

* **Audio & Video Playback:** Powered by LibVLCSharp for broad format compatibility.
* **Command-Line Configuration:** Heavily configurable at launch. Set the default theme, startup volume, default files, UI visibility, and lock the application into kiosk mode directly from the CLI.
* **Audio Visualizations:** Supports OpenGL-accelerated audio visualizations (using the companion `Jukebox-Visualizations` library).
* **Playlist Management:** Supports shuffling, looping, and loading entire directories dynamically.

## Command-Line Arguments

The Jukebox accepts the following switches on startup:

* `-light` or `-dark`: Force the UI into a specific theme variant.
* `-playlistlogo [filename.png]`: Render an image logo above the playlist area.
* `-random`: Enable random shuffle mode on startup.
* `-loop`: Loop the playlist continuously.
* `-hidecontrols`: Start with the bottom control bar hidden.
* `-volume [0-100]`: Define the initial startup volume.
* `-stayontop`: Force the window to remain always-on-top.
* `-fullscreen` or `-minimized`: Set the initial window state.
* `-kiosk`: Launch the application in a locked-down kiosk mode (hides close buttons, window chrome, and optionally borders).
* `-title [string]`: Override the default application window title.
* `-file [path]`: Provide a direct file or directory path to automatically load into the playlist and begin playing.
* `-forcevisualizer`: Force the visualizer to render even if the media type is not strictly detected as audio.

## Dependencies

* .NET 10.0
* Avalonia UI
* LibVLCSharp
* Jukebox-Visualizations (Local Project Reference)
