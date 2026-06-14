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

## Embedding as a UserControl

While the Jukebox is primarily designed as a standalone executable, it is also compiled as a standard Avalonia class library. This means you can embed the entire Jukebox player directly into your own Avalonia application's UI!

To do this, reference `Jukebox.dll` in your project and embed the `JukeboxControl` in your XAML:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:jukebox="clr-namespace:Jukebox.Views;assembly=Jukebox">
        
    <!-- Embed the Jukebox player anywhere in your UI -->
    <jukebox:JukeboxControl DataContext="{Binding MyJukeboxViewModel}" />
    
</Window>
```

*Note: When embedded as a UserControl, command-line window properties (like `-fullscreen` or `-stayontop`) are ignored. Your host application is responsible for managing the window state.*

## Dependencies

* **.NET 8.0**
* Avalonia UI
* LibVLCSharp
* [Jukebox-Visualizations](https://github.com/RobG66/Jukebox-Visualizations) (Referenced as a companion library)

---

## 🎨 Visualizations Setup (ProjectM)

In order for the music visualizer to work, the `Jukebox` requires the massive preset library (`.milk` files), textures, and unmanaged native dlls from the companion `Jukebox-Visualizations` repository. These assets must be placed inside a `ProjectM` folder **directly next to the Jukebox executable** (`Jukebox.exe`).

If you are setting up the Jukebox manually, you can quickly download and place the required `ProjectM` folder by running the following commands from your terminal. 

**Make sure you open your terminal inside the exact folder where the Jukebox application is located.**

### For Windows (PowerShell):
```powershell
# Clone the visualizations repository to a temporary folder
git clone --depth 1 https://github.com/RobG66/Jukebox-Visualizations.git temp_vis

# Move the massive ProjectM asset folder into the Jukebox directory
Move-Item -Path "temp_vis\ProjectM" -Destination ".\ProjectM" -Force

# Delete the temporary git repository
Remove-Item -Recurse -Force temp_vis
```

### For Linux / macOS (Bash):
```bash
# Clone the visualizations repository to a temporary folder
git clone --depth 1 https://github.com/RobG66/Jukebox-Visualizations.git temp_vis

# Move the massive ProjectM asset folder into the Jukebox directory
mv temp_vis/ProjectM ./ProjectM

# Delete the temporary git repository
rm -rf temp_vis
```

---

## 🔗 External Links & Requirements

* **[Gamelist-Manager](https://github.com/RobG66/Gamelist-Manager)**: The parent front-end application designed to launch and interface with this standalone Jukebox.
* **[Avalonia UI](https://avaloniaui.net/)**: The cross-platform UI framework powering the Jukebox interface.
* **[LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp)**: The cross-platform audio and video playback engine binding.
