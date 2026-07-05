using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Jukebox.Models;
using Jukebox.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Jukebox.Views;

public partial class PlaylistView : UserControl
{
    private readonly HashSet<int> _loadedRowIndices = new();
    private DateTime _lastScrollTime = DateTime.MinValue;
    private readonly Avalonia.Threading.DispatcherTimer _scrollDebounce;

    public PlaylistView()
    {
        InitializeComponent();

        _scrollDebounce = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Constants.ScrollDebouncePollMs)
        };
        _scrollDebounce.Tick += (_, _) =>
        {
            if ((DateTime.UtcNow - _lastScrollTime).TotalMilliseconds >= Constants.ScrollIdleMs)
            {
                _scrollDebounce.Stop();
                ReportVisibleRange();
            }
        };

        PlaylistDataGrid.LoadingRow += OnLoadingRow;
        PlaylistDataGrid.UnloadingRow += OnUnloadingRow;
    }

    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        _loadedRowIndices.Add(e.Row.Index);
        ScheduleReport();
    }

    private void OnUnloadingRow(object? sender, DataGridRowEventArgs e)
    {
        _loadedRowIndices.Remove(e.Row.Index);
        ScheduleReport();
    }

    private void ScheduleReport()
    {
        _lastScrollTime = DateTime.UtcNow;
        if (!_scrollDebounce.IsEnabled)
            _scrollDebounce.Start();
    }

    private void ReportVisibleRange()
    {
        if (DataContext is not JukeboxViewModel vm) return;
        if (_loadedRowIndices.Count == 0) return;

        int first = _loadedRowIndices.Min();
        int last = _loadedRowIndices.Max();

        vm.PlaylistViewModel.NotifyVisibleRange(first, last);
    }



    private void PlaylistDataGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is DataGrid dg &&
            dg.SelectedItem is JukeboxTrack track &&
            DataContext is JukeboxViewModel vm)
        {
            if (vm.PlayTrackCommand.CanExecute(track))
                vm.PlayTrackCommand.Execute(track);
        }
    }
}