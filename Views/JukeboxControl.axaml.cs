using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Jukebox.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Jukebox.Views;

public partial class JukeboxControl : UserControl
{
    #region Embedded Parameters
    public static readonly StyledProperty<string?> InitialFileProperty = AvaloniaProperty.Register<JukeboxControl, string?>(nameof(InitialFile));
    public string? InitialFile { get => GetValue(InitialFileProperty); set => SetValue(InitialFileProperty, value); }

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
    /// When true, the active visualizer is forced off even if available.
    /// Mirrors the <c>-novisualizer</c> command-line switch.
    /// </summary>
    public static readonly StyledProperty<bool> IsVisualizerDisabledProperty = AvaloniaProperty.Register<JukeboxControl, bool>(nameof(IsVisualizerDisabled));
    public bool IsVisualizerDisabled { get => GetValue(IsVisualizerDisabledProperty); set => SetValue(IsVisualizerDisabledProperty, value); }

    /// <summary>
    /// When true, the "show playing" OSD appears when the track changes.
    /// Defaults to true (OSD on) — Always Show by default. Mirrors the
    /// <c>-showplaying</c> command-line switch.
    /// </summary>
    public static readonly StyledProperty<bool> IsShowPlayingEnabledProperty = AvaloniaProperty.Register<JukeboxControl, bool>(nameof(IsShowPlayingEnabled), defaultValue: true);
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

        // Initialize the inactivity timer for the transport control bar.
        _inactivityTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Constants.ControlBarInactivitySeconds)
        };
        _inactivityTimer.Tick += OnInactivityTimerTick;

        this.PointerMoved   += (_, _) => ResetInactivity();
        this.PointerEntered += (_, _) => ResetInactivity();

        Loaded   += OnLoaded;
        // Stop the inactivity timer when control is unloaded to prevent leaks.
        Unloaded += OnUnloaded;

        // Drag/drop accepts supported media files and folders anywhere on the
        // control surface. The visible media page determines the destination:
        // Saved Playlists edits the selected saved collection; Queue, plugin,
        // or closed-drawer drops go to the runtime Play Queue.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble);
    }

    private void OnInactivityTimerTick(object? sender, EventArgs e)
    {
        if (DataContext is JukeboxViewModel vm && vm.IsAutoHideEnabled && !vm.IsControlsDisabled)
            vm.ControlBarHeight = Constants.HiddenControlBarHeight;
        _inactivityTimer.Stop();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        topLevel?.AddHandler(InputElement.KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        // Subscribe to window resize for visualizer suppression.
        // When the user drags the window edge, ContentView resizes every frame,
        // which resizes the visualizer GL surface → stutter. ContentView exposes
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
        // Stop the inactivity timer to release resources and prevent leaks.
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
        // the visualizer/video native control during the resize to prevent
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
        if (DataContext is not JukeboxViewModel vm) return;

        // When -nocontrols is active, keyboard shortcuts are disabled too.
        if (vm.IsControlsDisabled) return;

        if (e.Key == Key.F11 || (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Alt)))
        {
            vm.IsFullScreen = !vm.IsFullScreen;
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape) return;

        bool handled = false;
        if (vm.IsPlaylistVisible)
        {
            if (vm.PlaylistViewModel.ActiveTab == PlaylistTabType.Plugins)
            {
                // Escape behaves like Back: leave the browser surface first,
                // restoring whichever host panel destination was used last.
                vm.PlaylistViewModel.ActiveTabIndex = vm.PlaylistViewModel.LastHostTabIndex;
            }
            else
            {
                // A second Escape closes the host media panel.
                vm.IsPlaylistVisible = false;
            }

            handled = true;
        }
        if (vm.IsEqVisible)       { vm.IsEqVisible       = false; handled = true; }
        if (vm.IsPickerVisible)   { vm.IsPickerVisible   = false; handled = true; }

        if (!handled && vm.IsFullScreen)
        {
            vm.IsFullScreen = false;
            handled = true;
        }

        if (handled) e.Handled = true;
    }

    private void ResetInactivity()
    {
        if (DataContext is not JukeboxViewModel vm) return;
        // When -nocontrols is active, the transport bar never shows.
        if (vm.IsControlsDisabled) return;
        vm.ControlBarHeight = Constants.DefaultControlBarHeight;
        _inactivityTimer.Stop();
        if (vm.IsAutoHideEnabled)
            _inactivityTimer.Start();
    }

    #region Drag / Drop

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Accept file/folder drops only — no URL/text drops.
        // Avalonia 12: e.DataTransfer replaces e.Data, DataFormat.File
        // (singular) replaces DataFormats.Files (plural).
        e.DragEffects = e.DataTransfer.Formats.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not JukeboxViewModel vm) return;

        try
        {
            // Avalonia 12: TryGetFiles() is synchronous and returns
            // IEnumerable<IStorageItem>? (was GetFilesAsync() in Avalonia 11).
            var files = e.DataTransfer.TryGetFiles();
            if (files == null) return;

            var paths = new List<string>();
            foreach (var file in files)
            {
                // IStorageItem.Path returns a Uri. For local files, Uri.IsFile
                // is true and LocalPath gives the OS-native path string. This
                // avoids the TryGetLocalPath extension method which moved
                // between Avalonia 11 and 12.
                var uri = file.Path;
                if (uri != null && uri.IsFile)
                    paths.Add(uri.LocalPath);
            }
            if (paths.Count == 0) return;

            var target = vm.IsPlaylistVisible &&
                         vm.PlaylistViewModel.LastHostTabIndex == 1
                ? PlaylistTarget.SelectedSavedPlaylist
                : PlaylistTarget.PlayQueue;

            var importedTracks = await vm.PlaylistViewModel.ProcessAndAddFilesAsync(
                paths,
                target,
                vm.NoRecurse);

            // A play-queue drop may start the first new queue item when idle.
            // Editing a saved playlist must never alter current playback.
            if (target == PlaylistTarget.PlayQueue &&
                !vm.CanStop &&
                importedTracks.Count > 0)
            {
                var firstDropped = importedTracks[0];
                if (vm.PlayTrackCommand.CanExecute(firstDropped))
                    vm.PlayTrackCommand.Execute(firstDropped);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[JukeboxControl] Drop failed: {ex.Message}");
        }
    }

    #endregion

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == DataContextProperty && change.NewValue is JukeboxViewModel newVm)
        {
            if (this.IsSet(InitialFileProperty)) newVm.InitialFile = InitialFile;
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
