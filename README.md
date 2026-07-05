# Jukebox

Cross-platform desktop music and video player built with Avalonia UI (.NET 10.0), ManagedBass, libmpv, and libvgm.

## Features

- **Audio Playback**: Plays standard audio files (MP3, FLAC, WAV, OGG, M4A, WMA) using ManagedBass.
- **VGM Emulation**: Emulates and plays VGM, VGZ, and VGX video game music files using libvgm.
- **ZIP Playback**: Supports playing audio files directly from compressed `.zip` archives.
- **Video Playback**: Plays video files (MP4, MKV, AVI, WEBM) using a custom libmpv wrapper.
- **Audio Equalizer**: 10-band peaking equalizer for custom sound tuning (via BASS_FX PeakEQ).
- **Visualizations**: Optional music visualizations via projectM (loaded dynamically via reflection).
