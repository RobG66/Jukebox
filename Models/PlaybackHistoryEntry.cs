using System;

namespace Jukebox.Models;

/// <summary>
/// A successfully started track shown in the user's recent playback history.
/// The track is a snapshot so later queue or playlist edits cannot rewrite
/// history entries.
/// </summary>
public sealed class PlaybackHistoryEntry
{
    public required JukeboxTrack Track { get; init; }

    public DateTimeOffset PlayedAt { get; init; }

    public string PlayedAtText => PlayedAt.ToLocalTime().ToString("g");
}
