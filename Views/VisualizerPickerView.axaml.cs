using Avalonia.Controls;
using Avalonia.Input;
using Jukebox.ViewModels;
using System.Linq;

namespace Jukebox.Views;

public partial class VisualizerPickerView : UserControl
{
    public VisualizerPickerView()
    {
        InitializeComponent();
    }

    private void VisualizerTreeDataGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not JukeboxViewModel vm) return;

        var selected = vm.VisualizerViewModel.VisualizerSource
                         ?.RowSelection?.SelectedItems?.FirstOrDefault();

        if (selected is VisualizerFileViewModel fileVm)
            vm.VisualizerViewModel.SelectedVisualizerPath = fileVm.Path;

        // ContentView watches SelectedVisualizerPath and calls LoadPreset
    }

    private void OnSliderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Slider slider) return;
        if (DataContext is not JukeboxViewModel vm) return;

        var clickPos = e.GetPosition(slider);
        double ratio = slider.Bounds.Width > 0
            ? clickPos.X / slider.Bounds.Width
            : 0;
        ratio = System.Math.Clamp(ratio, 0, 1);

        double range = slider.Maximum - slider.Minimum;
        double newValue = slider.Minimum + (ratio * range);

        vm.VisualizerViewModel.VisualizerRandomizerIntervalSeconds = (int)System.Math.Round(newValue);
    }
}
