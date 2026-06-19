using CommunityToolkit.Mvvm.ComponentModel;

namespace Jukebox.Models;

public partial class JukeboxTrack : ObservableObject
{
    // Observable so the DataGrid updates when lazy tag loading writes back
    [ObservableProperty] private string _displayName = "Unknown Track";
    [ObservableProperty] private string _length = "—";
    [ObservableProperty] private string _bitrate = "—";

    public string FilePath { get; set; } = string.Empty;

    [ObservableProperty] private bool _isSelected;

    // Internal flag — not observable, the DataGrid never binds to it
    public bool IsTagged { get; set; }
}