using System.Collections.Generic;
using System.Linq;

namespace Jukebox;

/// <summary>
/// Application-wide constants. Merges the original media/extension/settings
/// constants with the named constants extracted during the smell-test refactor
/// (see Smell Test Report §6.4 and §7.1 item #1 — previously inline magic
/// numbers scattered across ViewModels and Views).
/// </summary>
public static class Constants
{
    // ── Media file extensions ──
    public static readonly string[] AudioExtensions = { ".mp3", ".flac", ".wav", ".ogg", ".m4a", ".wma" };

    public static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".avi", ".webm" };

    public static readonly HashSet<string> SupportedMediaExtensions =
        new HashSet<string>(AudioExtensions.Concat(VideoExtensions), System.StringComparer.OrdinalIgnoreCase);

    // ── Settings file locations ──
    public const string SettingsDirectoryName = "Jukebox";
    public const string EqSettingsFileName = "EqSettings.json";

    // ── Playback timer ──
    /// <summary>UI timer tick interval for playback position updates (ms).</summary>
    public const int PlaybackTimerIntervalMs = 250;

    // ── Show Playing OSD ──
    /// <summary>How long the OSD stays at full opacity before fading (ms).</summary>
    public const int OsdHoldMs = 3000;
    /// <summary>Number of fade-out steps used by the OSD animation.</summary>
    public const int OsdFadeSteps = 60;
    /// <summary>Starting opacity for the OSD fade animation (0.0 - 1.0).</summary>
    public const double OsdStartOpacity = 0.5;

    // ── Equalizer ──
    /// <summary>Number of EQ bands. Must match EqViewModel.SetupEqBands and the
    /// _eqFxHandles array size in PlaybackBASS.cs.</summary>
    public const int EqBandCount = 10;

    // ── Playlist tag reading ──
    /// <summary>Number of tracks to tag in one batch when scrolling.</summary>
    public const int TagBatchSize = 5;
    /// <summary>If playlist has this many tracks or fewer, tag them all immediately.</summary>
    public const int TagAllThreshold = 100;

    // ── Control bar auto-hide ──
    /// <summary>Default visible height of the transport control bar (px).</summary>
    public const double DefaultControlBarHeight = 65;
    /// <summary>Hidden height of the transport control bar (px).</summary>
    public const double HiddenControlBarHeight = 0;
    /// <summary>Inactivity period before the control bar auto-hides (seconds).</summary>
    public const int ControlBarInactivitySeconds = 5;

    // ── Playlist scroll tracking ──
    /// <summary>Idle period required before visible-range tag refresh fires (ms).</summary>
    public const int ScrollIdleMs = 500;
    /// <summary>Poll interval for the scroll debounce timer (ms).</summary>
    public const int ScrollDebouncePollMs = 50;

    // ── Disposal grace ──
    /// <summary>Maximum time to wait for playback disposal before giving up
    /// during window close. Prevents indefinite hangs on misbehaving backends.</summary>
    public const int DisposeTimeoutMs = 3000;
}
