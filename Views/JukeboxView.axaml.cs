using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Jukebox.Extensions;
using Jukebox.ViewModels;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Layout;
using System;
using System.ComponentModel;

namespace Jukebox.Views;

public partial class JukeboxView : Window
{
    // The compact host surface only needs the original 760x600 viewport.
    // Browser sizing combines the persistent 64-DIP navigation rail and
    // 275-DIP queue/playlist panel with the active plugin view's requested
    // minimum. Plugins without an explicit minimum retain the wider fallback.
    private const double CompactMinimumWidth = 760;
    private const double CompactMinimumHeight = 600;
    private const double BrowserHostChromeWidth = 339;
    private const double DefaultBrowserContentMinimumWidth = 800;
    private const double BrowserMinimumHeight = 680;

    private bool _isClosing = false;
    private JukeboxPlaylistViewModel? _playlistViewModel;
    private Control? _activeBrowserView;

    public JukeboxView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is JukeboxViewModel vm)
        {
            AttachPlaylistViewModel(vm.PlaylistViewModel);

            // Initialize the backend asynchronously and safely capture/log any failures.
            vm.InitializeBackendAsync().SafeFireAndForget(nameof(vm.InitializeBackendAsync));

            // Perform asynchronous initialization for visualizer and equalizer,
            // keeping synchronous I/O off the UI thread.
            // Visualizer initialization is a no-op when no visualizer plugin is installed.
            vm.VisualizerViewModel.LoadVisualizersAsync().SafeFireAndForget(nameof(vm.VisualizerViewModel.LoadVisualizersAsync));
            vm.VisualizerViewModel.InitializeAsync().SafeFireAndForget(nameof(vm.VisualizerViewModel.InitializeAsync));
            vm.EqViewModel.LoadAsync().SafeFireAndForget(nameof(vm.EqViewModel.LoadAsync));
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        AttachPlaylistViewModel(null);
        base.OnUnloaded(e);
    }

    private void AttachPlaylistViewModel(JukeboxPlaylistViewModel? viewModel)
    {
        if (ReferenceEquals(_playlistViewModel, viewModel))
        {
            return;
        }

        if (_playlistViewModel != null)
        {
            _playlistViewModel.PropertyChanged -= OnPlaylistPropertyChanged;
        }

        AttachActiveBrowserView(null);

        _playlistViewModel = viewModel;

        if (_playlistViewModel != null)
        {
            _playlistViewModel.PropertyChanged += OnPlaylistPropertyChanged;
        }

        AttachActiveBrowserView(GetActiveBrowserView());
        UpdateMinimumWindowSize();
    }

    private void OnPlaylistPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JukeboxPlaylistViewModel.ActiveTabIndex))
        {
            AttachActiveBrowserView(GetActiveBrowserView());
            UpdateMinimumWindowSize();
        }
    }

    private Control? GetActiveBrowserView()
    {
        if (_playlistViewModel == null)
        {
            return null;
        }

        int pluginIndex = _playlistViewModel.ActiveTabIndex - 2;
        return pluginIndex >= 0 && pluginIndex < _playlistViewModel.MediaBrowserTabs.Count
            ? _playlistViewModel.MediaBrowserTabs[pluginIndex].View
            : null;
    }

    private void AttachActiveBrowserView(Control? view)
    {
        if (ReferenceEquals(_activeBrowserView, view))
        {
            return;
        }

        if (_activeBrowserView != null)
        {
            _activeBrowserView.PropertyChanged -= OnActiveBrowserViewPropertyChanged;
        }

        _activeBrowserView = view;

        if (_activeBrowserView != null)
        {
            _activeBrowserView.PropertyChanged += OnActiveBrowserViewPropertyChanged;
        }
    }

    private void OnActiveBrowserViewPropertyChanged(
        object? sender,
        AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Layoutable.MinWidthProperty)
        {
            UpdateMinimumWindowSize();
        }
    }

    private void UpdateMinimumWindowSize()
    {
        bool browserIsActive =
            _playlistViewModel?.ActiveTab == PlaylistTabType.Plugins;
        double browserContentMinimum = _activeBrowserView?.MinWidth > 0
            ? _activeBrowserView.MinWidth
            : DefaultBrowserContentMinimumWidth;
        double browserMinimumWidth = BrowserHostChromeWidth + browserContentMinimum;

        MinWidth = browserIsActive ? browserMinimumWidth : CompactMinimumWidth;
        MinHeight = browserIsActive ? BrowserMinimumHeight : CompactMinimumHeight;

        // Applying a larger minimum does not consistently resize an already
        // shown native window on every platform. Grow a normal standalone
        // window explicitly, while leaving maximized/full-screen sizing alone.
        if (browserIsActive && WindowState == WindowState.Normal)
        {
            Width = Math.Max(Width, browserMinimumWidth);
            Height = Math.Max(Height, BrowserMinimumHeight);
        }
    }

    // We override OnClosing to prevent async void issues on window close.
    // The closing event is cancelled, and we perform the actual async cleanup
    // inside CloseAsync() before calling the base close.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_isClosing)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        _isClosing = true;

        _ = CloseAsync();
    }

    private async Task CloseAsync()
    {
        try
        {
            // ── Critical: detach the MpvView from the visual tree BEFORE
            // disposing MpvContext. If we don't, the OpenGlControlBase's
            // render callback may fire after the render context is freed,
            // causing AccessViolationException. ──
            // Walk the visual tree to find the ContentView and detach its
            // media host.
            var contentView = this.FindControl<ContentView>("ContentViewChild");
            // If not found by name, walk the visual tree.
            if (contentView == null)
            {
                contentView = this.GetVisualDescendants().OfType<ContentView>().FirstOrDefault();
            }
            contentView?.DetachMediaHost();

            if (DataContext is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync()
                    .AsTask()
                    .WaitAsync(TimeSpan.FromMilliseconds(Constants.DisposeTimeoutMs));
            }
            else if (DataContext is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (System.TimeoutException)
        {
            Debug.WriteLine($"[JukeboxView] DisposePlaybackAsync timed out after {Constants.DisposeTimeoutMs}ms; closing anyway.");
        }
        catch (System.Exception ex)
        {
            Debug.WriteLine($"[JukeboxView] DisposePlaybackAsync failed: {ex.Message}");
        }

        Close();
    }
}
