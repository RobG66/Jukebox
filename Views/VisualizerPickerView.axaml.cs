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
}
