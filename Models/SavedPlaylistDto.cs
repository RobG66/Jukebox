using System.Collections.Generic;

namespace Jukebox.Models;

public class SavedPlaylistDto
{
    public string Name { get; set; } = string.Empty;
    public List<SavedTrackDto> Tracks { get; set; } = new();
}
