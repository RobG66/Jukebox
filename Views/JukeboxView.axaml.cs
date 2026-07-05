using Avalonia.Controls;
using Avalonia.VisualTree;
using Jukebox.Extensions;
using Jukebox.ViewModels;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using System;

namespace Jukebox.Views;

public partial class JukeboxView : Window
{
    private bool _isClosing = false;

    public JukeboxView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is JukeboxViewModel vm)
        {
            // Initialize the backend asynchronously and safely capture/log any failures.
            vm.InitializeBackendAsync().SafeFireAndForget(nameof(vm.InitializeBackendAsync));

            // Perform asynchronous initialization for visualizer and equalizer,
            // keeping synchronous I/O off the UI thread.
            // Note: LoadVisualizersAsync no-ops if the ProjectM folder is absent.
            vm.VisualizerViewModel.LoadVisualizersAsync().SafeFireAndForget(nameof(vm.VisualizerViewModel.LoadVisualizersAsync));
            vm.VisualizerViewModel.InitializeAsync().SafeFireAndForget(nameof(vm.VisualizerViewModel.InitializeAsync));
            vm.EqViewModel.LoadAsync().SafeFireAndForget(nameof(vm.EqViewModel.LoadAsync));
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

            if (DataContext is JukeboxViewModel vm)
            {
                await vm.DisposePlaybackAsync()
                    .WaitAsync(TimeSpan.FromMilliseconds(Constants.DisposeTimeoutMs));
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
