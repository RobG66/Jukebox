using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Jukebox.Models;
using Jukebox.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Jukebox.Views;

public partial class SavedPlaylistsView : UserControl
{
    private readonly HashSet<int> _loadedRowIndices = new();
    private readonly Avalonia.Threading.DispatcherTimer _scrollDebounce;
    private DateTime _lastScrollTime = DateTime.MinValue;

    public SavedPlaylistsView()
    {
        InitializeComponent();

        _scrollDebounce = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Constants.ScrollDebouncePollMs)
        };
        _scrollDebounce.Tick += (_, _) =>
        {
            if ((DateTime.UtcNow - _lastScrollTime).TotalMilliseconds < Constants.ScrollIdleMs)
            {
                return;
            }

            _scrollDebounce.Stop();
            ReportVisibleRange();
        };

        LibraryDataGrid.LoadingRow += OnLoadingRow;
        LibraryDataGrid.UnloadingRow += OnUnloadingRow;
    }

    private void LibraryDataGrid_DoubleTapped(object? sender, TappedEventArgs e)
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

    private void LibraryContextMenu_Opened(object? sender, RoutedEventArgs e)
    {
        bool hasSelection = LibraryDataGrid.SelectedItems.Count > 0;
        bool hasCurrent = LibraryDataGrid.SelectedItem is JukeboxTrack;

        LibraryPlayNowMenuItem.IsEnabled = hasCurrent;
        LibraryPlayPlaylistMenuItem.IsEnabled = hasCurrent;
        LibraryQueueNextMenuItem.IsEnabled = hasSelection;
        LibraryQueueLastMenuItem.IsEnabled = hasSelection;
        LibraryRemoveMenuItem.IsEnabled = hasSelection;
        AddToPlaylistMenuItem.IsEnabled = hasSelection;

        AddToPlaylistMenuItem.Items.Clear();
        if (DataContext is not JukeboxViewModel vm)
        {
            return;
        }

        foreach (string playlistName in vm.PlaylistViewModel.SavedLibraryPlaylists)
        {
            var item = new MenuItem
            {
                Header = playlistName,
                Tag = playlistName
            };
            item.Click += AddToPlaylist_Click;
            AddToPlaylistMenuItem.Items.Add(item);
        }
    }

    private void LibraryPlayNow_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is JukeboxViewModel vm &&
            LibraryDataGrid.SelectedItem is JukeboxTrack track &&
            vm.PlaySavedTrackNowCommand.CanExecute(track))
        {
            vm.PlaySavedTrackNowCommand.Execute(track);
        }
    }

    private void LibraryPlayPlaylist_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is JukeboxViewModel vm &&
            LibraryDataGrid.SelectedItem is JukeboxTrack track &&
            vm.PlayTrackCommand.CanExecute(track))
        {
            vm.PlayTrackCommand.Execute(track);
        }
    }

    private void LibraryQueueNext_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is JukeboxViewModel vm &&
            vm.QueueSelectedNextCommand.CanExecute(LibraryDataGrid.SelectedItems))
        {
            vm.QueueSelectedNextCommand.Execute(LibraryDataGrid.SelectedItems);
        }
    }

    private void LibraryQueueLast_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is JukeboxViewModel vm &&
            vm.QueueSelectedLastCommand.CanExecute(LibraryDataGrid.SelectedItems))
        {
            vm.QueueSelectedLastCommand.Execute(LibraryDataGrid.SelectedItems);
        }
    }

    private void AddToPlaylist_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string playlistName } ||
            DataContext is not JukeboxViewModel vm)
        {
            return;
        }

        var selectedTracks = LibraryDataGrid.SelectedItems
            .Cast<object>()
            .OfType<JukeboxTrack>()
            .ToList();
        var request = new CopyToPlaylistRequest(playlistName, selectedTracks);

        if (vm.PlaylistViewModel.CopySelectedToPlaylistCommand.CanExecute(request))
        {
            vm.PlaylistViewModel.CopySelectedToPlaylistCommand.Execute(request);
        }
    }

    private void LibraryRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is JukeboxViewModel vm &&
            vm.PlaylistViewModel.RemoveLibrarySelectedCommand.CanExecute(LibraryDataGrid.SelectedItems))
        {
            vm.PlaylistViewModel.RemoveLibrarySelectedCommand.Execute(LibraryDataGrid.SelectedItems);
        }
    }

    private void MoveLibrarySelectedUp_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is JukeboxViewModel vm &&
            vm.PlaylistViewModel.MoveLibrarySelectedUpCommand.CanExecute(LibraryDataGrid.SelectedItems))
        {
            vm.PlaylistViewModel.MoveLibrarySelectedUpCommand.Execute(LibraryDataGrid.SelectedItems);
        }
    }

    private void MoveLibrarySelectedDown_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is JukeboxViewModel vm &&
            vm.PlaylistViewModel.MoveLibrarySelectedDownCommand.CanExecute(LibraryDataGrid.SelectedItems))
        {
            vm.PlaylistViewModel.MoveLibrarySelectedDownCommand.Execute(LibraryDataGrid.SelectedItems);
        }
    }

    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        _loadedRowIndices.Add(e.Row.Index);
        ScheduleVisibleRangeReport();
    }

    private void OnUnloadingRow(object? sender, DataGridRowEventArgs e)
    {
        _loadedRowIndices.Remove(e.Row.Index);
        ScheduleVisibleRangeReport();
    }

    private void ScheduleVisibleRangeReport()
    {
        _lastScrollTime = DateTime.UtcNow;
        if (!_scrollDebounce.IsEnabled)
        {
            _scrollDebounce.Start();
        }
    }

    private void ReportVisibleRange()
    {
        if (DataContext is not JukeboxViewModel vm || _loadedRowIndices.Count == 0)
        {
            return;
        }

        vm.PlaylistViewModel.NotifyVisibleRange(
            _loadedRowIndices.Min(),
            _loadedRowIndices.Max());
    }
}
