namespace Jukebox.Plugin.Abstractions;

/// <summary>
/// A play/add request passed from a plugin to the host via
/// <see cref="IJukeboxMediaBrowserContext.PlayNow"/> and
/// <see cref="IJukeboxMediaBrowserContext.AddToQueue"/>.
///
/// This is the only data shape that crosses the plugin boundary —
/// plugins never see the main app's <c>JukeboxTrack</c> type. The host
/// maps the request to a <c>JukeboxTrack</c> internally.
/// </summary>
public sealed class PlayRequest
{
    /// <summary>Display name shown in the host queue ("Prelude [FF7 OST]").</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Playable URL (HTTP stream, CDN URL, etc.). BASS plays this the
    /// same way it plays radio stream URLs.
    /// </summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>
    /// Stable URL or identifier used when the playable <see cref="Url"/> is
    /// temporary or signed. The host preserves this value when saving a
    /// queue or saved playlist and gives it back to the originating plugin for future URL
    /// resolution.
    /// </summary>
    public string? SourceUrl { get; init; }

    /// <summary>
    /// Optional plugin identifier responsible for resolving
    /// <see cref="SourceUrl"/>. The host context automatically supplies the
    /// current plugin id when this is omitted.
    /// </summary>
    public string? SourcePluginId { get; init; }

    /// <summary>Optional codec/MIME type ("audio/mpeg"). Shown in the Bitrate column.</summary>
    public string? Codec { get; init; }

    /// <summary>Optional bitrate in kbps. Paired with Codec for display.</summary>
    public int? Bitrate { get; init; }

    /// <summary>Optional genre/category shown in the Genre column.</summary>
    public string? Genre { get; init; }

    /// <summary>Optional ISO 3166-1 alpha-2 country code shown in the Country column.</summary>
    public string? Country { get; init; }
}
