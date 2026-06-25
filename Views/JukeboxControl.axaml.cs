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
        if (DataContext is JukeboxViewModel vm && vm.IsAutoHideEnabled)
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

        bool handled = false;
        if (vm.IsPlaylistVisible) { vm.IsPlaylistVisible = false; handled = true; }
        if (vm.IsEqVisible)       { vm.IsEqVisible       = false; handled = true; }
        if (vm.IsPickerVisible)   { vm.IsPickerVisible   = false; handled = true; }

        if (handled) e.Handled = true;
    }

    private void ResetInactivity()
    {
        if (DataContext is not JukeboxViewModel vm) return;
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
        }
        else if (DataContext is JukeboxViewModel vm)
        {
            if (change.Property == InitialFileProperty) vm.InitialFile = InitialFile;
            else if (change.Property == PlaylistLogoProperty) vm.PlaylistLogo = PlaylistLogo;
            else if (change.Property == InitialVolumeProperty) { vm.InitialVolume = InitialVolume; vm.Volume = InitialVolume; }
            else if (change.Property == IsRandomPlaybackProperty) vm.IsRandomPlayback = IsRandomPlayback;
            else if (change.Property == IsLoopEnabledProperty) vm.IsLoopEnabled = IsLoopEnabled;
            else if (change.Property == IsAutoHideEnabledProperty) vm.IsAutoHideEnabled = IsAutoHideEnabled;
        }
    }
}
