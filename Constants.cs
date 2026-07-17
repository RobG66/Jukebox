using System.Collections.Generic;
using System.Linq;

namespace Jukebox;

// Application-wide constants.
public static class Constants
{
    // ── Media file extensions ──
    public static readonly string[] AudioExtensions = { ".mp3", ".flac", ".wav", ".ogg", ".m4a", ".wma", ".vgz", ".vgm", ".vgx", ".zip" };

    public static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".avi", ".webm" };

    public static readonly HashSet<string> SupportedMediaExtensions =
        new HashSet<string>(AudioExtensions.Concat(VideoExtensions), System.StringComparer.OrdinalIgnoreCase);

    // ── Settings file locations ──
    public const string EqSettingsFileName = "EqSettings.json";

    // ── Playback timer ──
    // UI timer tick interval for playback position updates (ms).
    public const int PlaybackTimerIntervalMs = 250;

    // ── Show Playing OSD ──
    // How long the OSD stays at full opacity before fading (ms).
    public const int OsdHoldMs = 3000;
    // Number of fade-out steps used by the OSD animation.
    public const int OsdFadeSteps = 60;
    // Starting opacity for the OSD fade animation (0.0 - 1.0).
    public const double OsdStartOpacity = 0.7;

    // ── Equalizer ──
    // Number of EQ bands. Must match EqViewModel.SetupEqBands and the
    // _eqFxHandles array size in PlaybackBASS.cs.
    public const int EqBandCount = 10;

    // ── Playlist tag reading ──
    // Number of tracks to tag in one batch when scrolling.
    public const int TagBatchSize = 5;
    // If playlist has this many tracks or fewer, tag them all immediately.
    public const int TagAllThreshold = 100;

    // ── Control bar auto-hide ──
    // Default visible height of the transport control bar (px).
    public const double DefaultControlBarHeight = 65;
    // Hidden height of the transport control bar (px).
    public const double HiddenControlBarHeight = 0;
    // Inactivity period before the control bar auto-hides (seconds).
    public const int ControlBarInactivitySeconds = 5;

    // ── UI Layout ──
    // Default width of the slide-out side panels (px).
    public const double SidePanelWidth = 430;

    // ── Playlist scroll tracking ──
    // Idle period required before visible-range tag refresh fires (ms).
    public const int ScrollIdleMs = 500;
    // Poll interval for the scroll debounce timer (ms).
    public const int ScrollDebouncePollMs = 50;

    // ── Disposal grace ──
    // Maximum time to wait for playback disposal before giving up
    // during window close. Prevents indefinite hangs on misbehaving backends.
    public const int DisposeTimeoutMs = 3000;

    // ── Stream connection ──
    // Maximum time to wait for a URL stream (radio) to actually start
    // producing audio after PlayAsync is called. If no PCM data / playback
    // signal arrives within this window, the connection is treated as
    // failed, playback is aborted, and the user is shown an error dialog.
    public const int StreamConnectionTimeoutMs = 15000;
}
