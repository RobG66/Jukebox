using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jukebox.Extensions;
using Jukebox.Models;
using Jukebox.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Jukebox.ViewModels;

public partial class JukeboxViewModel : ViewModelBase, IDisposable
{
    #region Startup Properties
    public int     InitialVolume   { get; set; } = 100;
    public string? InitialFile     { get; set; }
    public bool    NoRecurse       { get; set; }
    public bool    StayOnTop       { get; set; }
    public int     ShowPlayingTimeout { get; set; } = 10;

    // ── New startup switches (set via command-line or JukeboxControl StyledProperty) ──
    /// <summary>
    /// When true, all UI controls (transport bar, side panels, keyboard
    /// shortcuts) are disabled — the window is strictly a playback surface.
    /// Set via <c>-nocontrols</c> command-line switch or the
    /// <see cref="Views.JukeboxControl.IsControlsDisabled"/> StyledProperty.
    /// </summary>
    public bool    NoControls      { get; set; }

    /// <summary>
    /// When true, the ProjectM visualizer is forced off even if the
    /// runtime is available. Set via <c>-novisualizer</c> command-line
    /// switch or the <see cref="Views.JukeboxControl.IsVisualizerDisabled"/>
    /// StyledProperty.
    /// </summary>
    public bool    NoVisualizer    { get; set; }

    /// <summary>
    /// When true, the visualizer preset randomizer is enabled at startup.
    /// Set via <c>-randompreset</c> command-line switch or the
    /// <see cref="Views.JukeboxControl.IsVisualizerRandomizerEnabled"/>
    /// StyledProperty.
    /// </summary>
    public bool    InitialRandomPreset { get; set; }

    /// <summary>
    /// The preset randomizer interval in seconds (10-60). Only used when
    /// <see cref="InitialRandomPreset"/> is true. Set via
    /// <c>-randompreset [time]</c> or the
    /// <see cref="Views.JukeboxControl.VisualizerRandomizerIntervalSeconds"/>
    /// StyledProperty.
    /// </summary>
    public int     InitialRandomPresetInterval { get; set; } = 10;
    // MPV does not use a shared-context concept.
    #endregion

    #region Sub-ViewModels
    public JukeboxPlaylistViewModel  PlaylistViewModel  { get; } = new();
    public JukeboxEqViewModel        EqViewModel        { get; } = new();
    public JukeboxVisualizerViewModel VisualizerViewModel { get; } = new();
    #endregion

    public Jukebox.Services.IStorageService? StorageService { get; set; }

    /// <summary>
    /// Reflection-based runtime that probes for the optional
    /// <c>JukeboxVisualizations.dll</c> wrapper + <c>ProjectM</c> preset
    /// folder. Exposed as a property so views (ContentView) can drive the
    /// visualizer control through the same abstraction. Defaults to the
    /// singleton <c>Jukebox.Services.VisualizerRuntime.Current</c>; tests
    /// can inject a stub via <see cref="Jukebox.Services.VisualizerRuntime.Override(IVisualizerRuntime?)"/>.
    /// </summary>
    // Note: the initializer uses the fully-qualified type name to avoid
    // ambiguity with this property's own name (the property name would
    // otherwise shadow the type name within instance members per the
    // C# member-lookup precedence rules).
    public IVisualizerRuntime VisualizerRuntime { get; set; } = Jukebox.Services.VisualizerRuntime.Current;

    // ── REFACTOR: OSD animation now lives in a dedicated service ──
    // (was TriggerShowPlayingOSDAsync, lines 119-160 in original)
    // See Smell Test Report §4.1 and §7.2 item #8.
    private readonly IShowPlayingService _showPlayingService;

    #region UI State
    [ObservableProperty] private bool    _isPlaylistVisible;
    [ObservableProperty] private bool    _isPickerVisible;
    [ObservableProperty] private bool    _isEqVisible       = false;
    [ObservableProperty] private bool    _isLoopEnabled;
    [ObservableProperty] private bool    _isRepeatEnabled   = false;
    [ObservableProperty] private bool    _isRandomPlayback  = false;
    [ObservableProperty] private bool    _isAutoHideEnabled = false;
    [ObservableProperty] private string? _playlistLogo;
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _playlistLogoBitmap;
    [ObservableProperty] private double  _controlBarHeight  = Constants.DefaultControlBarHeight;

    /// <summary>
    /// <c>true</c> when the optional <c>JukeboxVisualizations.dll</c>
    /// wrapper (in <c>lib/</c>) AND the <c>ProjectM</c> preset folder
    /// (with presets) are present at runtime. Drives:
    /// <list type="bullet">
    ///   <item>Visibility of the visualizer toggle button in the transport
    ///       bar (hidden when <c>false</c>).</item>
    ///   <item>Whether the visualizer picker side panel can be opened.</item>
    ///   <item>Whether the ProjectM control is created and bound when
    ///       audio is playing.</item>
    /// </list>
    /// When <c>false</c>, audio plays through BASS normally but no
    /// visualization is rendered in the MediaHost — the jukebox functions
    /// as a pure audio player with no ProjectM dependency.
    /// </summary>
    [ObservableProperty] private bool    _isVisualizerAvailable;

    /// <summary>
    /// When true, ALL UI controls are disabled — the transport bar, side
    /// panels (playlist, EQ, visualizer picker), and keyboard shortcuts
    /// are hidden/ignored. The window becomes strictly a playback surface
    /// (video or visualizer fills the entire area). Set via the
    /// <c>-nocontrols</c> command-line switch or the
    /// <see cref="Views.JukeboxControl.IsControlsDisabled"/> StyledProperty.
    /// </summary>
    [ObservableProperty] private bool    _isControlsDisabled;

    /// <summary>
    /// When true, the ProjectM visualizer is forced off even if the
    /// runtime probe found <c>JukeboxVisualizations.dll</c> + libprojectM.
    /// Audio still plays through BASS; the MediaHost stays empty during
    /// audio playback (same as when the visualizer is unavailable).
    /// Set via the <c>-novisualizer</c> command-line switch or the
    /// <see cref="Views.JukeboxControl.IsVisualizerDisabled"/> StyledProperty.
    /// </summary>
    [ObservableProperty] private bool    _isVisualizerDisabled;
    #endregion

    #region Show Playing OSD
    private readonly object _osdLock = new();
    private CancellationTokenSource? _showPlayingCts;
    // Default is false — the OSD only appears if the user passes
    // -showplaying on the command line or sets IsShowPlayingEnabled via
    // the JukeboxControl StyledProperty.
    [ObservableProperty] private bool   _isShowPlayingEnabled = false;
    [ObservableProperty] private string _showPlayingText    = "";
    [ObservableProperty] private bool   _isShowPlayingVisible = false;
    [ObservableProperty] private double _showPlayingOpacity = Constants.OsdStartOpacity;
    private bool _isDisposed;
    #endregion

    public JukeboxViewModel() : this(new ShowPlayingService())
    {
    }

    // Constructor added for testability — tests can inject a mock IShowPlayingService.
    // Production code uses the parameterless constructor above.
    public JukeboxViewModel(IShowPlayingService showPlayingService)
    {
        _showPlayingService = showPlayingService ?? throw new ArgumentNullException(nameof(showPlayingService));
        _showPlayingService.Changed += OnShowPlayingChanged;

        Volume = InitialVolume;
        PlaylistViewModel.PlaylistCleared += OnPlaylistCleared;
        // REFACTOR: Lambda replaced with named method so we can unsubscribe
        // in Dispose (was line 57-64, smell §4.1: Lambda event subscription
        // without unsubscribe).
        PlaylistViewModel.Playlist.CollectionChanged += OnPlaylistCollectionChanged;
    }

    private void OnPlaylistCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (!CanPause && !CanStop) // Stopped
        {
            CanPlay = PlaylistViewModel.Playlist.Count > 0;
        }
        _playedTracks.Clear();
    }

    private void OnShowPlayingChanged(object? sender, ShowPlayingEventArgs e)
    {
        // Forward service state to observable properties for binding.
        ShowPlayingText = e.Text;
        ShowPlayingOpacity = e.Opacity;
        IsShowPlayingVisible = e.IsVisible;
    }

    private void OnPlaylistCleared(object? sender, EventArgs e)
    {
        Stop();
        CurrentTrack = new JukeboxTrack { DisplayName = "GUI Design Mode - No Track Loaded" };
        CanPlay = false;
    }

    partial void OnIsPlaylistVisibleChanged(bool value)
    {
        if (value) IsPickerVisible = false;
    }

    partial void OnIsPickerVisibleChanged(bool value)
    {
        if (value) IsPlaylistVisible = false;
    }

    partial void OnPlaylistLogoChanged(string? value)
    {
        // REFACTOR: file existence check moved to a background thread to
        // avoid UI-thread stalls on network filesystems (smell §4.1: Direct
        // file existence check on UI thread). For a logo path, we accept
        // the tiny race in exchange for not blocking the UI.
        if (string.IsNullOrEmpty(value))
        {
            PlaylistLogoBitmap = null;
            return;
        }

        _ = LoadPlaylistLogoAsync(value);
    }

    private async Task LoadPlaylistLogoAsync(string path)
    {
        bool exists = await Task.Run(() => System.IO.File.Exists(path));
        if (!exists)
        {
            PlaylistLogoBitmap = null;
            return;
        }

        try
        {
            // Note: Avalonia.Media.Imaging.Bitmap must be created on UI thread.
            PlaylistLogoBitmap = new Avalonia.Media.Imaging.Bitmap(path);
        }
        catch (Exception ex)
        {
            // REFACTOR: Console.WriteLine → Debug.WriteLine (smell §4.1, §6.5)
            Debug.WriteLine($"Failed to load playlist logo: {ex.Message}");
            PlaylistLogoBitmap = null;
        }
    }

    partial void OnIsShowPlayingEnabledChanged(bool value)
    {
        if (value && CurrentTrack != null)
        {
            _showPlayingService.ShowAsync(CurrentTrack.DisplayName, ShowPlayingTimeout).SafeFireAndForget(nameof(_showPlayingService.ShowAsync));
        }
        else if (!value)
        {
            _showPlayingService.Hide();
        }
    }

    #region UI Toggle Commands
    [RelayCommand] private void TogglePlaylist()  => IsPlaylistVisible  = !IsPlaylistVisible;
    [RelayCommand] private void ToggleEq()        => IsEqVisible        = !IsEqVisible;
    [RelayCommand(CanExecute = nameof(CanTogglePicker))]
    private void TogglePicker()    => IsPickerVisible    = !IsPickerVisible;
    /// <summary>
    /// The visualizer picker can only be toggled when the optional
    /// visualizer runtime is available. The transport-bar button is also
    /// hidden in this case via a <c>IsVisible</c> binding, but the
    /// CanExecute guard prevents programmatic or keyboard invocation.
    /// </summary>
    private bool CanTogglePicker() => IsVisualizerAvailable;

    /// <summary>
    /// When the visualizer becomes unavailable (e.g. the ProjectM folder
    /// was removed mid-session), close the picker panel if it was open
    /// and re-evaluate the TogglePicker command's CanExecute. When it
    /// becomes available, just re-evaluate CanExecute so the button
    /// becomes clickable again.
    /// </summary>
    partial void OnIsVisualizerAvailableChanged(bool value)
    {
        if (!value && IsPickerVisible) IsPickerVisible = false;
        TogglePickerCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// When <see cref="IsControlsDisabled"/> is turned on, force-close any
    /// open panels and collapse the transport bar. Also re-evaluate the
    /// toggle commands so they can't be invoked programmatically.
    /// </summary>
    partial void OnIsControlsDisabledChanged(bool value)
    {
        if (value)
        {
            // Force-close any open panels.
            IsPlaylistVisible = false;
            IsPickerVisible   = false;
            IsEqVisible       = false;
            // Collapse the transport bar.
            ControlBarHeight  = Constants.HiddenControlBarHeight;
        }
        else
        {
            // Restore the default control bar height.
            ControlBarHeight = Constants.DefaultControlBarHeight;
        }
    }

    /// <summary>
    /// When <see cref="IsVisualizerDisabled"/> changes, re-probe the
    /// visualizer availability. The actual probe happens in
    /// <c>InitializeBackendAsync</c>, but if the user toggles this at
    /// runtime (via the JukeboxControl StyledProperty), we need to
    /// re-evaluate.
    /// </summary>
    partial void OnIsVisualizerDisabledChanged(bool value)
    {
        // Re-evaluate availability: if the runtime says it's available
        // but the user just disabled it, hide the button. If the user
        // just enabled it and the runtime is available, show the button.
        IsVisualizerAvailable = this.VisualizerRuntime.IsAvailable && !value;
    }

    partial void OnIsRandomPlaybackChanged(bool value)
    {
        if (value) _playedTracks.Clear();
    }

    [RelayCommand] private void ToggleAutoHide()  => IsAutoHideEnabled  = !IsAutoHideEnabled;

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        if (StorageService == null) return;
        var files = await StorageService.OpenFileDialogAsync(
            "Select Media Files",
            allowMultiple: true,
            audioExtensions: Constants.AudioExtensions,
            videoExtensions: Constants.VideoExtensions
        );
        if (files != null && files.Count > 0)
        {
            await PlaylistViewModel.ProcessAndAddFilesAsync(files, NoRecurse);
        }
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (StorageService == null) return;
        var folderPath = await StorageService.OpenFolderDialogAsync("Select Folder to Add");
        if (!string.IsNullOrEmpty(folderPath))
        {
            await PlaylistViewModel.ProcessAndAddFilesAsync(new List<string> { folderPath }, NoRecurse);
        }
    }
    #endregion

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        // REFACTOR: unsubscribe the previously-lambda'd handler (smell §4.1).
        PlaylistViewModel.Playlist.CollectionChanged -= OnPlaylistCollectionChanged;
        PlaylistViewModel.PlaylistCleared -= OnPlaylistCleared;
        _showPlayingService.Changed -= OnShowPlayingChanged;

        lock (_osdLock)
        {
            if (_showPlayingCts != null)
            {
                try { _showPlayingCts.Cancel(); } catch { }
                _showPlayingCts.Dispose();
                _showPlayingCts = null;
            }
        }
        _showPlayingService.Hide();
        VisualizerViewModel?.Dispose();

        // REFACTOR: fire-and-forget dispose is now observed via SafeFireAndForget
        // (was `_ = DisposePlaybackAsync();` — smell §4.1 Critical: fire-and-forget
        // DisposePlaybackAsync). The dispose still happens asynchronously to
        // avoid blocking, but exceptions are now captured and logged.
        DisposePlaybackAsync().SafeFireAndForget(nameof(DisposePlaybackAsync));
    }
}
