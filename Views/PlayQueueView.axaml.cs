using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Jukebox.Models;
using Jukebox.ViewModels;

namespace Jukebox.Views;

public partial class PlayQueueView : UserControl
{
    public PlayQueueView()
    {
        InitializeComponent();
    }

    private void PlayQueueDataGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not DataGrid { SelectedItem: JukeboxTrack track } ||
            DataContext is not JukeboxViewModel vm)
        {
            return;
        }

        if (vm.PlayTrackCommand.CanExecute(track))
        {
            vm.PlayTrackCommand.Execute(track);
        }
    }

    private void PlayQueueContextMenu_Opened(object? sender, RoutedEventArgs e)
    {
        bool hasSelection = PlayQueueDataGrid.SelectedItems.Count > 0;
        PlayQueuePlayNowMenuItem.IsEnabled = PlayQueueDataGrid.SelectedItem is JukeboxTrack;
        PlayQueueRenameMenuItem.IsEnabled = PlayQueueDataGrid.SelectedItem is JukeboxTrack;
        PlayQueueRemoveMenuItem.IsEnabled = hasSelection;
    }

    private void PlayQueueRename_Click(object? sender, RoutedEventArgs e)
    {
        RenamePlayQueueSelected_Click(sender, e);
    }

    private void RenamePlayQueueSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is JukeboxViewModel vm &&
            vm.PlaylistViewModel.RenamePlayQueueSelectedCommand.CanExecute(PlayQueueDataGrid.SelectedItems))
        {
            vm.PlaylistViewModel.RenamePlayQueueSelectedCommand.Execute(PlayQueueDataGrid.SelectedItems);
        }
    }

    private void PlayQueuePlayNow_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is JukeboxViewModel vm &&
            PlayQueueDataGrid.SelectedItem is JukeboxTrack track &&
            vm.PlayTrackCommand.CanExecute(track))
        {
            vm.PlayTrackCommand.Execute(track);
        }
    }

    private void PlayQueueRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is JukeboxViewModel vm &&
            vm.PlaylistViewModel.RemovePlayQueueSelectedCommand.CanExecute(PlayQueueDataGrid.SelectedItems))
        {
            vm.PlaylistViewModel.RemovePlayQueueSelectedCommand.Execute(PlayQueueDataGrid.SelectedItems);
        }
    }

    private void PlayQueueSave_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is JukeboxViewModel vm &&
            vm.PlaylistViewModel.SavePlayQueueAsPlaylistCommand.CanExecute(null))
        {
            vm.PlaylistViewModel.SavePlayQueueAsPlaylistCommand.Execute(null);
        }
    }

    private void PlayQueueClear_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is JukeboxViewModel vm &&
            vm.PlaylistViewModel.ClearPlayQueueCommand.CanExecute(null))
        {
            vm.PlaylistViewModel.ClearPlayQueueCommand.Execute(null);
        }
    }

    private void MovePlayQueueSelectedUp_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is JukeboxViewModel vm &&
            vm.PlaylistViewModel.MovePlayQueueSelectedUpCommand.CanExecute(PlayQueueDataGrid.SelectedItems))
        {
            vm.PlaylistViewModel.MovePlayQueueSelectedUpCommand.Execute(PlayQueueDataGrid.SelectedItems);
        }
    }

    private void MovePlayQueueSelectedDown_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is JukeboxViewModel vm &&
            vm.PlaylistViewModel.MovePlayQueueSelectedDownCommand.CanExecute(PlayQueueDataGrid.SelectedItems))
        {
            vm.PlaylistViewModel.MovePlayQueueSelectedDownCommand.Execute(PlayQueueDataGrid.SelectedItems);
        }
    }
}
