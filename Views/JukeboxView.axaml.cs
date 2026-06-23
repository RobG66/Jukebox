using Avalonia.Controls;
using Jukebox.ViewModels;
using System.Threading.Tasks;
using Avalonia.Interactivity;

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
            _ = vm.InitializeBackendAsync();
            _ = vm.VisualizerViewModel.LoadVisualizersAsync();
        }
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_isClosing)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        _isClosing = true;

        if (DataContext is JukeboxViewModel vm)
        {
            await vm.DisposePlaybackAsync();

            // Allow Avalonia to process the binding change, 
            // which tells LibVLC to cleanly detach from the window handle
            await Task.Delay(100);
        }

        Close();
    }
}
