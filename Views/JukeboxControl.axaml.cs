using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Jukebox.ViewModels;
using System;

namespace Jukebox.Views;

public partial class JukeboxControl : UserControl
{
    private readonly Avalonia.Threading.DispatcherTimer _inactivityTimer;

    public JukeboxControl()
    {
        InitializeComponent();

        _inactivityTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _inactivityTimer.Tick += (_, _) =>
        {
            if (DataContext is JukeboxViewModel vm && vm.IsAutoHideEnabled)
                vm.ControlBarHeight = 0;
            _inactivityTimer.Stop();
        };

        this.PointerMoved   += (_, _) => ResetInactivity();
        this.PointerEntered += (_, _) => ResetInactivity();

        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        topLevel?.AddHandler(InputElement.KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        topLevel?.RemoveHandler(InputElement.KeyDownEvent, OnPreviewKeyDown);
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
        vm.ControlBarHeight = 65;
        _inactivityTimer.Stop();
        if (vm.IsAutoHideEnabled)
            _inactivityTimer.Start();
    }
}
