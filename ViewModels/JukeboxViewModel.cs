using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jukebox.Extensions;
using Jukebox.Models;
using Jukebox.Services;
using Jukebox.Plugin.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Jukebox.ViewModels;

public partial class JukeboxViewModel : ViewModelBase, IDisposable, IAsyncDisposable
{
    #region Startup Properties
    public int InitialVolume { get; set; } = 100;
    public string? InitialFile { get; set; }
    public bool NoRecurse { get; set; }
    /// <summary>
    /// When true, the main window is kept topmost (always-on-top). Bound
    /// two-way from the transport-bar toggle button so the user can flip
    /// it at runtime, and also bound to <c>Window.Topmost</c> in
    /// <see cref="Views.JukeboxView"/>. Initially set via the
    /// <c>-stayontop</c> command-line switch.
    /// </summary>
    [ObservableProperty] private bool _stayOnTop;
    public int ShowPlayingTimeout { get; set; } = 10;


    public VgmPlaybackEngine? VgmEngine { get; set; }

    // ── New startup switches (set via command-line or JukeboxControl StyledProperty) ──
    /// <summary>
    /// When true, all UI controls (transport bar, side panels, keyboard
    /// shortcuts) are disabled — the window is strictly a playback surface.
    /// Set via <c>-nocontrols</c> command-line switch or the
    /// <see cref="Views.JukeboxControl.IsControlsDisabled"/> StyledProperty.
    /// </summary>
    public bool NoControls { get; set; }

    /// <summary>
    /// When true, the active visualizer is forced off even if it is
    /// available. Set via <c>-novisualizer</c> command-line
    /// switch or the <see cref="Views.JukeboxControl.IsVisualizerDisabled"/>
    /// StyledProperty.
    /// </summary>
    public bool NoVisualizer { get; set; }

    /// <summary>
    /// When true, the visualizer preset randomizer is enabled at startup.
    /// Set via <c>-randompreset</c> command-line switch or the
    /// <see cref="Views.JukeboxControl.IsVisualizerRandomizerEnabled"/>
    /// StyledProperty.
    /// </summary>
    public bool InitialRandomPreset { get; set; }

    /// <summary>
    /// The preset randomizer interval in seconds (10-60). Only used when
    /// <see cref="InitialRandomPreset"/> is true. Set via
    /// <c>-randompreset [time]</c> or the
    /// <see cref="Views.JukeboxControl.VisualizerRandomizerIntervalSeconds"/>
    /// StyledProperty.
    /// </summary>
    public int InitialRandomPresetInterval { get; set; } = 10;
    // MPV does not use a shared-context concept.
    #endregion

    #region Sub-ViewModels
    public JukeboxPlaylistViewModel PlaylistViewModel { get; }
    public JukeboxEqViewModel EqViewModel { get; } = new();
    public JukeboxVisualizerViewModel VisualizerViewModel { get; }
    #endregion

    public Jukebox.Services.IStorageService? StorageService { get; set; }

    /// <summary>
    /// List of loaded visualizer plugins.
    /// </summary>
    private IReadOnlyList<IJukeboxVisualizerPlugin> _visualizerPlugins = Array.Empty<IJukeboxVisualizerPlugin>();
    public IReadOnlyList<IJukeboxVisualizerPlugin> VisualizerPlugins
    {
        get => _visualizerPlugins;
        set
        {
            if (SetProperty(ref _visualizerPlugins, value))
            {
                IsVisualizerAvailable = _visualizerPlugins.Any(p => p.IsAvailable) && !IsVisualizerDisabled;
            }
        }
    }

    /// <summary>
    /// Currently active visualizer plugin.
    /// </summary>
    private IJukeboxVisualizerPlugin? _activeVisualizer;
    public IJukeboxVisualizerPlugin? ActiveVisualizer
    {
        get => _activeVisualizer;
        set => SetProperty(ref _activeVisualizer, value);
    }

    // Service to manage the "Now Playing" OSD animations.
    private readonly IShowPlayingService _showPlayingService;
    private readonly IUserDialogService _dialogService;

    #region UI State
    [ObservableProperty] private bool _isPlaylistVisible;
    [ObservableProperty] private bool _isPickerVisible;
    [ObservableProperty] private bool _isEqVisible = false;
    [ObservableProperty] private bool _isLoopEnabled;
    [ObservableProperty] private bool _isRepeatEnabled = false;
    [ObservableProperty] private bool _isRandomPlayback = false;
    [ObservableProperty] private bool _isAutoHideEnabled = false;
    private Avalonia.Controls.WindowState _previousWindowState = Avalonia.Controls.WindowState.Normal;
    [ObservableProperty] private Avalonia.Controls.WindowState _windowState = Avalonia.Controls.WindowState.Normal;
    [ObservableProperty] private bool _isFullScreen = false;

    /// <summary>
    /// True while a URL stream (radio) is being opened and has not yet
    /// started producing audio. Drives the "Connecting..." overlay in
    /// ContentView. Cleared on success (engine raises PlaybackStarted)
    /// or on failure (timeout, HTTP error, SSL error, etc.).
    /// </summary>
    [ObservableProperty] private bool _isConnecting;

    /// <summary>
    /// Human-readable host/URL being connected to, shown in the overlay
    /// (e.g. "Connecting to stream.radiox.sk:8443..."). Set together with
    /// <see cref="IsConnecting"/> at the start of stream playback.
    /// </summary>
    [ObservableProperty] private string _connectingMessage = "";

    partial void OnWindowStateChanged(Avalonia.Controls.WindowState value)
    {
        var targetFullScreen = (value == Avalonia.Controls.WindowState.FullScreen);
        if (IsFullScreen != targetFullScreen)
        {
            IsFullScreen = targetFullScreen;
        }
    }

    partial void OnIsFullScreenChanged(bool value)
    {
        if (value)
        {
            if (WindowState != Avalonia.Controls.WindowState.FullScreen)
            {
                _previousWindowState = WindowState;
                WindowState = Avalonia.Controls.WindowState.FullScreen;
            }
        }
        else
        {
            if (WindowState == Avalonia.Controls.WindowState.FullScreen)
            {
                WindowState = _previousWindowState != Avalonia.Controls.WindowState.FullScreen
                    ? _previousWindowState
                    : Avalonia.Controls.WindowState.Normal;
            }
        }
    }
    [ObservableProperty] private double _controlBarHeight = Constants.DefaultControlBarHeight;

    /// <summary>
    /// <c>true</c> when an installed visualizer plugin reports that it is
    /// available. Drives:
    /// <list type="bullet">
    ///   <item>Visibility of the visualizer toggle button in the transport
    ///       bar (hidden when <c>false</c>).</item>
    ///   <item>Whether the visualizer picker side panel can be opened.</item>
    ///   <item>Whether the plugin control is created and bound when
    ///       audio is playing.</item>
    /// </list>
    /// When <c>false</c>, audio plays through BASS normally but no
    /// visualization is rendered in the MediaHost — the jukebox functions
    /// as a pure audio player with no visualizer dependency.
    /// </summary>
    [ObservableProperty] private bool _isVisualizerAvailable;

    /// <summary>
    /// When true, ALL UI controls are disabled — the transport bar, side
    /// panels (playlist, EQ, visualizer picker), and keyboard shortcuts
    /// are hidden/ignored. The window becomes strictly a playback surface
    /// (video or visualizer fills the entire area). Set via the
    /// <c>-nocontrols</c> command-line switch or the
    /// <see cref="Views.JukeboxControl.IsControlsDisabled"/> StyledProperty.
    /// </summary>
    [ObservableProperty] private bool _isControlsDisabled;

    /// <summary>
    /// When true, the active visualizer is forced off even if available.
    /// Audio still plays through BASS; the MediaHost stays empty during
    /// audio playback (same as when the visualizer is unavailable).
    /// Set via the <c>-novisualizer</c> command-line switch or the
    /// <see cref="Views.JukeboxControl.IsVisualizerDisabled"/> StyledProperty.
    /// </summary>
    [ObservableProperty] private bool _isVisualizerDisabled;
    #endregion

    #region Show Playing OSD
    private readonly object _osdLock = new();
    private CancellationTokenSource? _showPlayingCts;
    // Default is true — the OSD appears by default (Always Show).
    // Standalone mode explicitly disables it at startup unless -showplaying is passed,
    // which aligns with the command-line interface expectations.
    [ObservableProperty] private bool _isShowPlayingEnabled = true;
    [ObservableProperty] private string _showPlayingText = "";
    [ObservableProperty] private bool _isShowPlayingVisible = false;
    [ObservableProperty] private double _showPlayingOpacity = Constants.OsdStartOpacity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlayingModeTooltip))]
    private ShowPlayingMode _showPlayingMode = ShowPlayingMode.Always;

    public string ShowPlayingModeTooltip => ShowPlayingMode switch
    {
        ShowPlayingMode.Off => "Show Now Playing: Off",
        ShowPlayingMode.Briefly => "Show Now Playing: Show Briefly",
        ShowPlayingMode.Always => "Show Now Playing: Always Show",
        _ => "Show Now Playing"
    };

    private bool _isDisposed;
    #endregion

    public JukeboxViewModel() : this(new ShowPlayingService(), null)
    {
    }

    // Constructor added for testability — tests can inject mocks.
    // Production code uses the parameterless constructor above.
    public JukeboxViewModel(IShowPlayingService showPlayingService, IUserDialogService? dialogService = null)
    {
        _showPlayingService = showPlayingService ?? throw new ArgumentNullException(nameof(showPlayingService));
        _dialogService = dialogService ?? new UserDialogService();
        _showPlayingService.Changed += OnShowPlayingChanged;

        Volume = InitialVolume;

        // Create sub-VMs with the dialog service so they don't reference
        // Jukebox.Views directly for error/confirm dialogs.
        PlaylistViewModel = new JukeboxPlaylistViewModel(_dialogService);
        VisualizerViewModel = new JukeboxVisualizerViewModel(Jukebox.Services.PathProvider.Current, _dialogService);
        VisualizerViewModel.SetHost(this);


        PlaylistViewModel.PlayQueue.CollectionChanged += OnPlayQueueCollectionChanged;
        PlaylistViewModel.PlayQueueReplaced += OnPlayQueueReplaced;
        PlaylistViewModel.PlayQueueCleared += OnPlayQueueCleared;
        PlaylistViewModel.PlayingQueueTrackRemoved += OnPlayingQueueTrackRemoved;
        VgmEngine = new VgmPlaybackEngine();
    }

    private void OnPlayQueueCollectionChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (var track in e.OldItems.OfType<JukeboxTrack>())
            {
                _playedTracks.Remove(track);
            }
        }

        if (!CanPause && !CanStop) // Stopped
        {
            CanPlay = PlaylistViewModel.PlayQueue.Count > 0;
        }
    }

    private void OnPlayQueueReplaced(object? sender, EventArgs e)
    {
        _playedTracks.Clear();

        // Queue replacement is an explicit playback-order change. Stop the
        // previous item before the caller selects a track from the new queue.
        if (CanPause || CanStop || IsConnecting)
        {
            Stop();
        }

        CurrentTrack = null;
        CanPlay = PlaylistViewModel.PlayQueue.Count > 0;
    }

    private void OnShowPlayingChanged(object? sender, ShowPlayingEventArgs e)
    {
        // Forward service state to observable properties for binding.
        ShowPlayingText = e.Text;
        ShowPlayingOpacity = e.Opacity;
        IsShowPlayingVisible = e.IsVisible;
    }

    private void OnPlayQueueCleared(object? sender, EventArgs e)
    {
        _playedTracks.Clear();
        Stop();
        CurrentTrack = null;
        CanPlay = false;
    }

    private void OnPlayingQueueTrackRemoved(object? sender, EventArgs e)
    {
        Stop();
        CurrentTrack = null;
        CanPlay = PlaylistViewModel.PlayQueue.Count > 0;
    }

    partial void OnIsPlaylistVisibleChanged(bool value)
    {
        if (value) IsPickerVisible = false;
    }

    partial void OnIsPickerVisibleChanged(bool value)
    {
        if (value) IsPlaylistVisible = false;

        // Suspend the randomizer timer while the picker panel is open so
        // the current preset stays put while the user browses / adds to
        // favorites. Resume on close if the toggle is still on. This
        // bypasses IsVisualizerRandomizerEnabled entirely — the toggle
        // state is preserved, only the timer is paused/resumed.
        if (value) VisualizerViewModel.SuspendTimer();
        else VisualizerViewModel.ResumeTimer();
    }

    partial void OnShowPlayingModeChanged(ShowPlayingMode value)
    {
        IsShowPlayingEnabled = (value != ShowPlayingMode.Off);

        if (value != ShowPlayingMode.Off)
        {
            if (CurrentTrack != null)
            {
                bool always = (value == ShowPlayingMode.Always);
                _showPlayingService.ShowAsync(CurrentTrack.DisplayName, ShowPlayingTimeout, always).SafeFireAndForget(nameof(_showPlayingService.ShowAsync));
            }
        }
        else
        {
            _showPlayingService.Hide();
        }
    }

    partial void OnIsShowPlayingEnabledChanged(bool value)
    {
        if (value && ShowPlayingMode == ShowPlayingMode.Off)
        {
            ShowPlayingMode = ShowPlayingMode.Always;
        }
        else if (!value && ShowPlayingMode != ShowPlayingMode.Off)
        {
            ShowPlayingMode = ShowPlayingMode.Off;
        }
    }

    #region UI Toggle Commands
    [RelayCommand]
    private void CycleShowPlayingMode()
    {
        ShowPlayingMode = ShowPlayingMode switch
        {
            ShowPlayingMode.Always => ShowPlayingMode.Briefly,
            ShowPlayingMode.Briefly => ShowPlayingMode.Off,
            ShowPlayingMode.Off => ShowPlayingMode.Always,
            _ => ShowPlayingMode.Always
        };
    }

    [RelayCommand] private void TogglePlaylist() => IsPlaylistVisible = !IsPlaylistVisible;
    [RelayCommand] private void ToggleEq() => IsEqVisible = !IsEqVisible;
    [RelayCommand(CanExecute = nameof(CanTogglePicker))]
    private void TogglePicker() => IsPickerVisible = !IsPickerVisible;
    /// <summary>
    /// The visualizer picker can only be toggled when the optional
    /// visualizer runtime is available. The transport-bar button is also
    /// hidden in this case via a <c>IsVisible</c> binding, but the
    /// CanExecute guard prevents programmatic or keyboard invocation.
    /// </summary>
    private bool CanTogglePicker() => IsVisualizerAvailable;

    /// <summary>
    /// When the visualizer becomes unavailable, close the picker panel if it was open
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
            IsPickerVisible = false;
            IsEqVisible = false;
            // Collapse the transport bar.
            ControlBarHeight = Constants.HiddenControlBarHeight;
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
        IsVisualizerAvailable = VisualizerPlugins.Any(p => p.IsAvailable) && !value;
    }

    partial void OnIsRandomPlaybackChanged(bool value)
    {
        if (value) _playedTracks.Clear();
    }

    [RelayCommand] private void ToggleAutoHide() => IsAutoHideEnabled = !IsAutoHideEnabled;

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
            await PlaylistViewModel.ProcessAndAddFilesAsync(
                files,
                GetInteractiveImportTarget(),
                NoRecurse);
        }
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (StorageService == null) return;
        var folderPath = await StorageService.OpenFolderDialogAsync("Select Folder to Add");
        if (!string.IsNullOrEmpty(folderPath))
        {
            await PlaylistViewModel.ProcessAndAddFilesAsync(
                new[] { folderPath },
                GetInteractiveImportTarget(),
                NoRecurse);
        }
    }

    private PlaylistTarget GetInteractiveImportTarget()
        => PlaylistViewModel.LastHostTabIndex == 1
            ? PlaylistTarget.SelectedSavedPlaylist
            : PlaylistTarget.PlayQueue;

    // ── Plugin Browsers ─────────────────────────────────────────────
    // Plugin browser commands are supplied dynamically by installed plugins.
    // ────────────────────────────────────────────────────────────────
    #endregion

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        DisposeSyncInternal();

        // Trigger fire-and-forget disposal only because we are not disposing asynchronously
        DisposePlaybackAsync().SafeFireAndForget(nameof(DisposePlaybackAsync));
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        DisposeSyncInternal();

        // Safely await the playback engine disposal
        await DisposePlaybackAsync();
        GC.SuppressFinalize(this);
    }

    private void DisposeSyncInternal()
    {
        PlaylistViewModel.PlayQueue.CollectionChanged -= OnPlayQueueCollectionChanged;
        PlaylistViewModel.PlayQueueReplaced -= OnPlayQueueReplaced;
        PlaylistViewModel.PlayQueueCleared -= OnPlayQueueCleared;
        PlaylistViewModel.PlayingQueueTrackRemoved -= OnPlayingQueueTrackRemoved;
        PlaylistViewModel.DisposeMediaBrowsers();
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
        VgmEngine?.Dispose();
    }
}
