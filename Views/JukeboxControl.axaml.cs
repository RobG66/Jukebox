using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Jukebox.ViewModels;
using System;

namespace Jukebox.Views;

public partial class JukeboxControl : UserControl
{
    #region Embedded Parameters
    public static readonly StyledProperty<string?> InitialFileProperty = AvaloniaProperty.Register<JukeboxControl, string?>(nameof(InitialFile));
    public string? InitialFile { get => GetValue(InitialFileProperty); set => SetValue(InitialFileProperty, value); }

    public static readonly StyledProperty<string?> PlaylistLogoProperty = AvaloniaProperty.Register<JukeboxControl, string?>(nameof(PlaylistLogo));
    public string? PlaylistLogo { get => GetValue(PlaylistLogoProperty); set => SetValue(PlaylistLogoProperty, value); }

    public static readonly StyledProperty<int> InitialVolumeProperty = AvaloniaProperty.Register<JukeboxControl, int>(nameof(InitialVolume), 100);
    public int InitialVolume { get => GetValue(InitialVolumeProperty); set => SetValue(InitialVolumeProperty, value); }

    public static readonly StyledProperty<bool> IsRandomPlaybackProperty = AvaloniaProperty.Register<JukeboxControl, bool>(nameof(IsRandomPlayback));
    public bool IsRandomPlayback { get => GetValue(IsRandomPlaybackProperty); set => SetValue(IsRandomPlaybackProperty, value); }

    public static readonly StyledProperty<bool> IsLoopEnabledProperty = AvaloniaProperty.Register<JukeboxControl, bool>(nameof(IsLoopEnabled));
    public bool IsLoopEnabled { get => GetValue(IsLoopEnabledProperty); set => SetValue(IsLoopEnabledProperty, value); }

    public static readonly StyledProperty<bool> IsAutoHideEnabledProperty = AvaloniaProperty.Register<JukeboxControl, bool>(nameof(IsAutoHideEnabled));
    public bool IsAutoHideEnabled { get => GetValue(IsAutoHideEnabledProperty); set => SetValue(IsAutoHideEnabledProperty, value); }

    // ── New switches (mirror the command-line args) ──

    /// <summary>
    /// When true, all UI controls (transport bar, side panels, keyboard
    /// shortcuts) are disabled — strictly a playback window. Mirrors the
    /// <c>-nocontrols</c> command-line switch.
    /// </summary>
    public static readonly StyledProperty<bool> IsControlsDisabledProperty = AvaloniaProperty.Register<JukeboxControl, bool>(nameof(IsControlsDisabled));
    public bool IsControlsDisabled { get => GetValue(IsControlsDisabledProperty); set => SetValue(IsControlsDisabledProperty, value); }

    /// <summary>
    /// When true, the ProjectM visualizer is forced off even if available.
    /// Mirrors the <c>-novisualizer</c> command-line switch.
    /// </summary>
    public static readonly StyledProperty<bool> IsVisualizerDisabledProperty = AvaloniaProperty.Register<JukeboxControl, bool>(nameof(IsVisualizerDisabled));
    public bool IsVisualizerDisabled { get => GetValue(IsVisualizerDisabledProperty); set => SetValue(IsVisualizerDisabledProperty, value); }

    /// <summary>
    /// When true, the "show playing" OSD appears when the track changes.
    /// Defaults to false (OSD off) — the user must opt in. Mirrors the
    /// <c>-showplaying</c> command-line switch.
    /// </summary>
    public static readonly StyledProperty<bool> IsShowPlayingEnabledProperty = AvaloniaProperty.Register<JukeboxControl, bool>(nameof(IsShowPlayingEnabled));
    public bool IsShowPlayingEnabled { get => GetValue(IsShowPlayingEnabledProperty); set => SetValue(IsShowPlayingEnabledProperty, value); }

    /// <summary>
    /// How long (in seconds) the "show playing" OSD holds at full opacity
    /// before fading. Default 10. Mirrors the <c>-showplaying [timeout]</c>
    /// command-line switch's optional value.
    /// </summary>
    public static readonly StyledProperty<int> ShowPlayingTimeoutProperty = AvaloniaProperty.Register<JukeboxControl, int>(nameof(ShowPlayingTimeout), 10);
    public int ShowPlayingTimeout { get => GetValue(ShowPlayingTimeoutProperty); set => SetValue(ShowPlayingTimeoutProperty, value); }

    /// <summary>
    /// When true, the visualizer preset randomizer is enabled at startup.
    /// Mirrors the <c>-randompreset</c> command-line switch.
    /// </summary>
    public static readonly StyledProperty<bool> IsVisualizerRandomizerEnabledProperty = AvaloniaProperty.Register<JukeboxControl, bool>(nameof(IsVisualizerRandomizerEnabled));
    public bool IsVisualizerRandomizerEnabled { get => GetValue(IsVisualizerRandomizerEnabledProperty); set => SetValue(IsVisualizerRandomizerEnabledProperty, value); }

    /// <summary>
    /// Preset randomizer interval in seconds (10-60). Default 10. Mirrors
    /// the <c>-randompreset [time]</c> command-line switch's optional value.
    /// </summary>
    public static readonly StyledProperty<int> VisualizerRandomizerIntervalSecondsProperty = AvaloniaProperty.Register<JukeboxControl, int>(nameof(VisualizerRandomizerIntervalSeconds), 10);
    public int VisualizerRandomizerIntervalSeconds { get => GetValue(VisualizerRandomizerIntervalSecondsProperty); set => SetValue(VisualizerRandomizerIntervalSecondsProperty, value); }
    #endregion

    private readonly Avalonia.Threading.DispatcherTimer _inactivityTimer;
    // Window reference for SizeChanged subscription (window-resize suppression).
    private Window? _hostWindow;

    public JukeboxControl()
    {
        InitializeComponent();

        // REFACTOR: magic number 5 seconds → Constants.ControlBarInactivitySeconds (smell §5.2, §6.4).
        _inactivityTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Constants.ControlBarInactivitySeconds)
        };
        _inactivityTimer.Tick += OnInactivityTimerTick;

        this.PointerMoved   += (_, _) => ResetInactivity();
        this.PointerEntered += (_, _) => ResetInactivity();

        Loaded   += OnLoaded;
        // REFACTOR: also stop the timer on Unloaded (was smell §5.2 Warning:
        // Inactivity timer never disposed).
        Unloaded += OnUnloaded;
    }

    private void OnInactivityTimerTick(object? sender, EventArgs e)
    {
        if (DataContext is JukeboxViewModel vm && vm.IsAutoHideEnabled && !vm.IsControlsDisabled)
            // REFACTOR: magic number 0 → Constants.HiddenControlBarHeight (smell §5.2, §6.4).
            vm.ControlBarHeight = Constants.HiddenControlBarHeight;
        _inactivityTimer.Stop();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        topLevel?.AddHandler(InputElement.KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        // Subscribe to window resize for ProjectM suppression.
        // When the user drags the window edge, ContentView resizes every frame,
        // which resizes the ProjectM GL surface → stutter. ContentView exposes
        // a SuppressNativeRenderDuringLayoutTransition() method that hides the
        // native control during the resize and restores it once settled.
        _hostWindow = topLevel as Window;
        if (_hostWindow != null)
        {
            _hostWindow.PropertyChanged += OnHostWindowPropertyChanged;
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        topLevel?.RemoveHandler(InputElement.KeyDownEvent, OnPreviewKeyDown);
        // REFACTOR: stop the inactivity timer to release the Tick handler
        // closure (was smell §5.2 Warning: Inactivity timer never disposed).
        _inactivityTimer.Stop();

        // Unsubscribe from window resize notifications.
        if (_hostWindow != null)
        {
            _hostWindow.PropertyChanged -= OnHostWindowPropertyChanged;
            _hostWindow = null;
        }
    }

    private void OnHostWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // Window resize fires Width/Height property changes. When this happens,
        // trigger the layout-transition suppression in ContentView (which hides
        // the ProjectM/VideoView native control during the resize to prevent
        // per-frame GL surface resizes).
        if (e.Property == Window.WidthProperty || e.Property == Window.HeightProperty)
        {
            if (ContentViewChild is ContentView contentView)
            {
                contentView.NotifyWindowResizing();
            }
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not JukeboxViewModel vm) return;

        // When -nocontrols is active, keyboard shortcuts are disabled too.
        if (vm.IsControlsDisabled) return;

        bool handled = false;
        if (vm.IsPlaylistVisible) { vm.IsPlaylistVisible = false; handled = true; }
        if (vm.IsEqVisible)       { vm.IsEqVisible       = false; handled = true; }
        if (vm.IsPickerVisible)   { vm.IsPickerVisible   = false; handled = true; }

        if (handled) e.Handled = true;
    }

    private void ResetInactivity()
    {
        if (DataContext is not JukeboxViewModel vm) return;
        // When -nocontrols is active, the transport bar never shows.
        if (vm.IsControlsDisabled) return;
        // REFACTOR: magic number 65 → Constants.DefaultControlBarHeight (smell §5.2, §6.4).
        vm.ControlBarHeight = Constants.DefaultControlBarHeight;
        _inactivityTimer.Stop();
        if (vm.IsAutoHideEnabled)
            _inactivityTimer.Start();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == DataContextProperty && change.NewValue is JukeboxViewModel newVm)
        {
            if (this.IsSet(InitialFileProperty)) newVm.InitialFile = InitialFile;
            if (this.IsSet(PlaylistLogoProperty)) newVm.PlaylistLogo = PlaylistLogo;
            if (this.IsSet(InitialVolumeProperty)) { newVm.InitialVolume = InitialVolume; newVm.Volume = InitialVolume; }
            if (this.IsSet(IsRandomPlaybackProperty)) newVm.IsRandomPlayback = IsRandomPlayback;
            if (this.IsSet(IsLoopEnabledProperty)) newVm.IsLoopEnabled = IsLoopEnabled;
            if (this.IsSet(IsAutoHideEnabledProperty)) newVm.IsAutoHideEnabled = IsAutoHideEnabled;
            if (this.IsSet(IsControlsDisabledProperty)) newVm.IsControlsDisabled = IsControlsDisabled;
            if (this.IsSet(IsVisualizerDisabledProperty)) newVm.IsVisualizerDisabled = IsVisualizerDisabled;
            if (this.IsSet(IsShowPlayingEnabledProperty)) { newVm.IsShowPlayingEnabled = IsShowPlayingEnabled; newVm.ShowPlayingTimeout = ShowPlayingTimeout; }
            if (this.IsSet(ShowPlayingTimeoutProperty)) newVm.ShowPlayingTimeout = ShowPlayingTimeout;
            if (this.IsSet(IsVisualizerRandomizerEnabledProperty)) { newVm.VisualizerViewModel.IsVisualizerRandomizerEnabled = IsVisualizerRandomizerEnabled; newVm.VisualizerViewModel.VisualizerRandomizerIntervalSeconds = VisualizerRandomizerIntervalSeconds; }
            if (this.IsSet(VisualizerRandomizerIntervalSecondsProperty)) newVm.VisualizerViewModel.VisualizerRandomizerIntervalSeconds = VisualizerRandomizerIntervalSeconds;
        }
        else if (DataContext is JukeboxViewModel vm)
        {
            if (change.Property == InitialFileProperty) vm.InitialFile = InitialFile;
            else if (change.Property == PlaylistLogoProperty) vm.PlaylistLogo = PlaylistLogo;
            else if (change.Property == InitialVolumeProperty) { vm.InitialVolume = InitialVolume; vm.Volume = InitialVolume; }
            else if (change.Property == IsRandomPlaybackProperty) vm.IsRandomPlayback = IsRandomPlayback;
            else if (change.Property == IsLoopEnabledProperty) vm.IsLoopEnabled = IsLoopEnabled;
            else if (change.Property == IsAutoHideEnabledProperty) vm.IsAutoHideEnabled = IsAutoHideEnabled;
            else if (change.Property == IsControlsDisabledProperty) vm.IsControlsDisabled = IsControlsDisabled;
            else if (change.Property == IsVisualizerDisabledProperty) vm.IsVisualizerDisabled = IsVisualizerDisabled;
            else if (change.Property == IsShowPlayingEnabledProperty) { vm.IsShowPlayingEnabled = IsShowPlayingEnabled; vm.ShowPlayingTimeout = ShowPlayingTimeout; }
            else if (change.Property == ShowPlayingTimeoutProperty) vm.ShowPlayingTimeout = ShowPlayingTimeout;
            else if (change.Property == IsVisualizerRandomizerEnabledProperty) { vm.VisualizerViewModel.IsVisualizerRandomizerEnabled = IsVisualizerRandomizerEnabled; vm.VisualizerViewModel.VisualizerRandomizerIntervalSeconds = VisualizerRandomizerIntervalSeconds; }
            else if (change.Property == VisualizerRandomizerIntervalSecondsProperty) vm.VisualizerViewModel.VisualizerRandomizerIntervalSeconds = VisualizerRandomizerIntervalSeconds;
        }
    }
}
