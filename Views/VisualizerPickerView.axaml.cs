using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Input;
using Avalonia.Media;
using Jukebox.ViewModels;
using System;
using System.Linq;

namespace Jukebox.Views;

public partial class VisualizerPickerView : UserControl
{
    public VisualizerPickerView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is JukeboxViewModel vm)
        {
            var source = new HierarchicalTreeDataGridSource<VisualizerNodeViewModel>(vm.VisualizerViewModel.RootNodes)
            {
                Columns =
                {
                    new HierarchicalExpanderColumn<VisualizerNodeViewModel>(
                        new TextColumn<VisualizerNodeViewModel, string>(
                            "Visualizations",
                            x => x.Name,
                            new GridLength(1, GridUnitType.Star),
                            new TextColumnOptions<VisualizerNodeViewModel>
                            {
                                TextTrimming = TextTrimming.CharacterEllipsis
                            }),
                        x => x is VisualizerFolderViewModel f ? f.Children : null,
                        x => x.IsDirectory)
                }
            };

            source.RowSelection!.SelectionChanged += (s, ev) =>
            {
                vm.VisualizerViewModel.SelectedNode = source.RowSelection?.SelectedItems?.FirstOrDefault();
            };

            VisualizerTreeDataGrid.Source = source;
        }
    }

    private void VisualizerTreeDataGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not JukeboxViewModel vm) return;

        if (VisualizerTreeDataGrid.Source is HierarchicalTreeDataGridSource<VisualizerNodeViewModel> source)
        {
            var selected = source.RowSelection?.SelectedItems?.FirstOrDefault();
            if (selected is VisualizerFileViewModel fileVm)
            {
                vm.VisualizerViewModel.SelectedVisualizerPath = fileVm.Path;
            }
        }
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
