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
            // REFACTOR: SafeFireAndForget instead of fire-and-forget init calls
            // (was smell §5.3 Warning: Fire-and-forget InitializeBackendAsync on Loaded).
            // Backend init / visualizer load failures are now captured and logged.
            vm.InitializeBackendAsync().SafeFireAndForget(nameof(vm.InitializeBackendAsync));
            vm.VisualizerViewModel.LoadVisualizersAsync().SafeFireAndForget(nameof(vm.VisualizerViewModel.LoadVisualizersAsync));

            // REFACTOR: VisualizerViewModel.InitializeAsync and EqViewModel.LoadAsync
            // are the new async entry points replacing sync IO in constructors
            // (was smell §4.5 Critical: Synchronous file IO on UI thread in constructor,
            // and §4.7 Warning: Synchronous JSON file IO in constructor).
            vm.VisualizerViewModel.InitializeAsync().SafeFireAndForget(nameof(vm.VisualizerViewModel.InitializeAsync));
            vm.EqViewModel.LoadAsync().SafeFireAndForget(nameof(vm.EqViewModel.LoadAsync));
        }
    }

    // REFACTOR: this was `protected override async void OnClosing(...)`.
    // async void is dangerous — exceptions after the first await crash the
    // process. Replaced with a synchronous override that defers the actual
    // cleanup to a fire-and-forget Task (with proper error capture).
    // (was smell §5.3 Critical: async void OnClosing)
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
