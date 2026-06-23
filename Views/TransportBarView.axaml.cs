using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Jukebox.ViewModels;

namespace Jukebox.Views;

public partial class TransportBarView : UserControl
{
    public TransportBarView()
    {
        InitializeComponent();

        var slider = this.FindControl<Slider>("PlaybackSlider");
        if (slider != null)
        {
            slider.AddHandler(Thumb.DragStartedEvent, OnSliderDragStarted);
            slider.AddHandler(Thumb.DragCompletedEvent, OnSliderDragCompleted);
        }
    }

    private void OnSliderDragStarted(object? sender, RoutedEventArgs e)
    {
        if (DataContext is JukeboxViewModel vm)
        {
            vm.IsSeeking = true;
        }
    }

    private void OnSliderDragCompleted(object? sender, RoutedEventArgs e)
    {
        if (DataContext is JukeboxViewModel vm)
        {
            vm.IsSeeking = false;
        }
    }
}
