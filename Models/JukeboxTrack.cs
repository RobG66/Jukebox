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

    // FilePath is the source currently handed to the playback engine. For
    // online media this may be a short-lived URL produced by a plugin
    // resolver, so it must not be treated as the permanent identity of the
    // track.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaybackSource))]
    private string _filePath = string.Empty;

    // Stable URL/identifier used for persistence and future re-resolution.
    // Local files normally leave this null and use FilePath directly.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaybackSource))]
    private string? _originalUrl;

    // Identifies the plugin that created/resolves this track. Keeping this on
    // the host model avoids probing every plugin when playback resolution is
    // requested.
    [ObservableProperty] private string? _sourcePluginId;

    public string PlaybackSource =>
        !string.IsNullOrWhiteSpace(OriginalUrl) ? OriginalUrl : FilePath;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string _genre = "—";
    [ObservableProperty] private string _country = "—";

    // Internal flag — not observable, the DataGrid never binds to it
    public bool IsTagged { get; set; }

}
