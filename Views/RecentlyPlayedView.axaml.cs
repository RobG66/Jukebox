using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Jukebox.Models;
using Jukebox.ViewModels;

namespace Jukebox.Views;

public partial class RecentlyPlayedView : UserControl
{
    public RecentlyPlayedView()
    {
        InitializeComponent();
    }

    private void HistoryDataGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        PlaySelected();
    }

    private void HistoryContextMenu_Opened(object? sender, RoutedEventArgs e)
    {
        bool hasSelection = HistoryDataGrid.SelectedItem is PlaybackHistoryEntry;
        HistoryPlayNowMenuItem.IsEnabled = hasSelection;
        HistoryAddToQueueMenuItem.IsEnabled = hasSelection;
    }

    private void HistoryPlayNow_Click(object? sender, RoutedEventArgs e)
    {
        PlaySelected();
    }

    private void HistoryAddToQueue_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is JukeboxViewModel vm &&
            HistoryDataGrid.SelectedItem is PlaybackHistoryEntry entry &&
            vm.QueueHistoryEntryCommand.CanExecute(entry))
        {
            vm.QueueHistoryEntryCommand.Execute(entry);
        }
    }

    private void PlaySelected()
    {
        if (DataContext is JukeboxViewModel vm &&
            HistoryDataGrid.SelectedItem is PlaybackHistoryEntry entry &&
            vm.PlayHistoryEntryCommand.CanExecute(entry))
        {
            vm.PlayHistoryEntryCommand.Execute(entry);
        }
    }
}
