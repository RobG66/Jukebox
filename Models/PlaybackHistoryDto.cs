using System;
using System.Collections.Generic;

namespace Jukebox.Models;

public sealed class PlaybackHistoryDto
{
    public List<PlaybackHistoryEntryDto> Entries { get; set; } = new();
}

public sealed class PlaybackHistoryEntryDto
{
    public DateTimeOffset PlayedAt { get; set; }

    public SavedTrackDto Track { get; set; } = new();
}
