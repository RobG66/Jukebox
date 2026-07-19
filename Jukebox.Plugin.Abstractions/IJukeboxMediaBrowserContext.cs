using System.Collections.Generic;
using System.Threading.Tasks;

namespace Jukebox.Plugin.Abstractions;

/// <summary>
/// Services the host app provides to each plugin at startup. Passed to
/// <see cref="IJukeboxMediaBrowser.InitializeAsync"/>. Plugins use this
/// to add tracks to the host play queue, play tracks, and find out where to
/// store their private data.
///
/// This interface intentionally lives in the Abstractions assembly so
/// plugins don't need to reference the main Jukebox assembly (which
/// would create a circular dependency).
/// </summary>
public interface IJukeboxMediaBrowserContext
{
    /// <summary>
    /// Absolute path to the plugin's private data directory
    /// (e.g. <c>&lt;appdir&gt;/Cache/Plugins/khinsider/</c>).
    /// Guaranteed to exist and be writable. Use this for cache files,
    /// favorites, settings — anything the plugin needs to persist.
    /// </summary>
    string PluginDataDirectory { get; }

    /// <summary>
    /// Insert one track after the current queue item and start playback without
    /// discarding the existing host play queue. If no queue item is current,
    /// the track is inserted at the beginning.
    /// </summary>
    void PlayNow(PlayRequest request);

    /// <summary>
    /// Replaces the host play queue and starts playing the item matching
    /// <paramref name="activeSource"/>.
    /// </summary>
    void ReplaceQueueAndPlay(IEnumerable<PlayRequest> queue, string activeSource);

    /// <summary>
    /// Log a message to the app's debug output (visible in Visual
    /// Studio's Output window or in <c>dotnet run</c> console output).
    /// Use for diagnostics — not for user-facing status.
    /// </summary>
    void Log(string message);

    /// <summary>
    /// Append an item to the end of the host play queue without interrupting playback.
    /// </summary>
    void AddToQueue(PlayRequest request);

    /// <summary>
    /// Append multiple items to the end of the host play queue.
    /// </summary>
    void AddRangeToQueue(IEnumerable<PlayRequest> requests);

    /// <summary>
    /// Update the transient playable URL of an existing host queue item while
    /// preserving its stable source URL.
    /// </summary>
    void UpdateTrackUrl(string originalUrl, string resolvedUrl);

    /// <summary>
    /// Show a confirmation dialog to the user with Yes/No choices.
    /// Returns true if the user confirmed, false otherwise.
    /// </summary>
    Task<bool> ShowConfirmAsync(string title, string message);

    /// <summary>
    /// The URL or file path of the currently playing track in the host application, if any.
    /// </summary>
    string? CurrentlyPlayingUrl { get; }

    /// <summary>
    /// The display title of the currently playing track in the host application, if any.
    /// </summary>
    string? CurrentlyPlayingTitle { get; }

    /// <summary>
    /// Triggered when the currently playing track changes in the host application.
    /// </summary>
    event System.Action? CurrentlyPlayingChanged;
}
