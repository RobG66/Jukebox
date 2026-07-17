using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
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
            // Click-to-seek: clicking on the slider track moves the
            // playhead to the clicked position.
            slider.PointerPressed += OnPlaybackSliderPointerPressed;
        }

        // Click-to-set on the volume slider.
        var volumeSlider = this.FindControl<Slider>("VolumeSlider");
        if (volumeSlider != null)
        {
            volumeSlider.PointerPressed += OnVolumeSliderPointerPressed;
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

    /// <summary>
    /// Click-to-seek: when the user clicks on the playback slider track,
    /// calculate the position from the click's X coordinate and seek.
    /// </summary>
    private void OnPlaybackSliderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Slider slider) return;
        if (DataContext is not JukeboxViewModel vm) return;
        if (slider.Maximum <= 0) return;

        var clickPos = e.GetPosition(slider);
        double ratio = slider.Bounds.Width > 0
            ? clickPos.X / slider.Bounds.Width
            : 0;
        ratio = Math.Clamp(ratio, 0, 1);

        // Setting PlaybackPosition triggers SeekToPosition (see VM).
        vm.PlaybackPosition = ratio * slider.Maximum;
    }

    /// <summary>
    /// Click-to-set: when the user clicks on the volume slider track,
    /// set the volume to the clicked position.
    /// </summary>
    private void OnVolumeSliderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Slider slider) return;
        if (DataContext is not JukeboxViewModel vm) return;

        var clickPos = e.GetPosition(slider);
        double ratio = slider.Bounds.Width > 0
            ? clickPos.X / slider.Bounds.Width
            : 0;
        ratio = Math.Clamp(ratio, 0, 1);

        vm.Volume = ratio * slider.Maximum;
    }
}
