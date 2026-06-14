using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Jukebox.ViewModels;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;
using System;
using System.Runtime.InteropServices;

namespace Jukebox.Views;

public partial class JukeboxControl : UserControl
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private readonly DispatcherTimer _mousePoller = new();
    private POINT _lastMousePos;

    private JukeboxViewModel? _boundViewModel;

    public JukeboxControl()
    {
        InitializeComponent();

        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        DataContextChanged += OnDataContextChanged;

        _mousePoller.Interval = TimeSpan.FromMilliseconds(100);
        _mousePoller.Tick += OnMousePollerTick;
        _mousePoller.Start();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        WireViewModel();
    }

    private void WireViewModel()
    {
        if (DataContext is JukeboxViewModel vm)
        {
            if (_boundViewModel == vm) return;
            UnwireViewModel();
            _boundViewModel = vm;

            vm.SetProjectMControl(ProjectMView);
            
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel != null)
            {
                vm.SetStorageProvider(topLevel.StorageProvider);
            }

            vm.MediaPlayerCreated += OnMediaPlayerCreated;
            vm.ErrorOccurred += OnErrorOccurred;
            vm.CloseRequested += OnCloseRequested;
            vm.PropertyChanged += OnViewModelPropertyChanged;

            vm.RequestPlayer();
        }
        else
        {
            UnwireViewModel();
        }
    }

    private void UnwireViewModel()
    {
        if (_boundViewModel != null)
        {
            _boundViewModel.MediaPlayerCreated -= OnMediaPlayerCreated;
            _boundViewModel.ErrorOccurred -= OnErrorOccurred;
            _boundViewModel.CloseRequested -= OnCloseRequested;
            _boundViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _boundViewModel = null;
        }
    }

    private void OnMousePollerTick(object? sender, EventArgs e)
    {
        if (!IsVisible) return;

        if (GetCursorPos(out var p))
        {
            if (p.X != _lastMousePos.X || p.Y != _lastMousePos.Y)
            {
                _lastMousePos = p;
                
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return;
                
                var clientPos = topLevel.PointToClient(new PixelPoint(p.X, p.Y));
                if (clientPos.X >= 0 && clientPos.X <= Bounds.Width &&
                    clientPos.Y >= 0 && clientPos.Y <= Bounds.Height)
                {
                    if (DataContext is JukeboxViewModel vm)
                    {
                        vm.ResetAutoHideTimer();
                    }
                }
            }
        }
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        WireViewModel();
        if (_boundViewModel != null && TopLevel.GetTopLevel(this) is { } topLevel)
        {
            _boundViewModel.SetStorageProvider(topLevel.StorageProvider);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Removed dynamic window resizing for the picker as it is now an overlay
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        VideoView.MediaPlayer = null;

        if (_boundViewModel != null)
        {
            _ = _boundViewModel.DisposeAsync();
        }
        UnwireViewModel();
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is Window window)
        {
            window.Close();
        }
    }

    private void OnMediaPlayerCreated(MediaPlayer? mediaPlayer)
    {
        if (DataContext is JukeboxViewModel vm)
        {
            if (vm.VisualizationsEnabled && vm.HasProjectM)
            {
                VideoView.MediaPlayer = null;
                VideoView.IsVisible = false;
                ProjectMWrapper.IsVisible = true;
            }
            else
            {
                VideoView.MediaPlayer = mediaPlayer;
                VideoView.IsVisible = true;
                ProjectMWrapper.IsVisible = false;
            }
            
            if (mediaPlayer != null)
            {
                vm.NotifyPlayerAttached();
            }
        }
    }

    private async void OnErrorOccurred(string message)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this) as Window;
            if (topLevel != null)
            {
                await ThreeButtonDialogView.ShowErrorAsync(
                    "Jukebox Error",
                    message,
                    owner: topLevel);
            }
        }
        catch (Exception)
        {
        }
    }

    private void PresetList_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is JukeboxViewModel vm && vm.SelectedPreset != null)
        {
            vm.ApplyPresetCommand.Execute(null);
        }
    }
}
