using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Jukebox.Models;

public partial class JukeboxTrack : ObservableObject
{
    // Observable so the DataGrid updates when lazy tag loading writes back
    [ObservableProperty] private string _displayName = "Unknown Track";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLength))]
    private TimeSpan _length = TimeSpan.Zero;

    public string DisplayLength => Length.TotalSeconds == 0 ? "—" : $"{(int)Length.TotalMinutes}:{Length.Seconds:D2}";
    [ObservableProperty] private string _bitrate = "—";

    public string FilePath { get; set; } = string.Empty;

    [ObservableProperty] private bool _isSelected;

    // Internal flag — not observable, the DataGrid never binds to it
    public bool IsTagged { get; set; }
}