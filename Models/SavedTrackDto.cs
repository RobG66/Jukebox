using System;

namespace Jukebox.Models;

public class SavedTrackDto
{
    public string DisplayName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string? OriginalUrl { get; set; }
    public string? SourcePluginId { get; set; }
    public TimeSpan Length { get; set; }
    public string Bitrate { get; set; } = "—";
    public string Genre { get; set; } = "—";
    public string Country { get; set; } = "—";
    public bool IsTagged { get; set; }
}
