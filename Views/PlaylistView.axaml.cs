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
    private const int ScrollIdleMs = 500;

    private readonly HashSet<int> _loadedRowIndices = new();
    private DateTime _lastScrollTime = DateTime.MinValue;
    private readonly Avalonia.Threading.DispatcherTimer _scrollDebounce;

    public PlaylistView()
    {
        InitializeComponent();

        _scrollDebounce = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _scrollDebounce.Tick += (_, _) =>
        {
            if ((DateTime.UtcNow - _lastScrollTime).TotalMilliseconds >= ScrollIdleMs)
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

    private async void AddFiles_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null || DataContext is not JukeboxViewModel vm) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Media Files",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Media Files")
                {
                    Patterns = new[] { "*.mp3", "*.flac", "*.wav", "*.ogg", "*.m4a", "*.wma",
                                       "*.mp4", "*.mkv", "*.avi", "*.webm" }
                }
            }
        });

        if (files == null || files.Count == 0) return;

        var paths = files.Select(f => f.TryGetLocalPath())
                         .Where(p => !string.IsNullOrEmpty(p))
                         .ToList();

        if (paths.Count > 0)
            await vm.PlaylistViewModel.ProcessAndAddFilesAsync(paths!, vm.NoRecurse);
    }

    private async void AddFolder_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null || DataContext is not JukeboxViewModel vm) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Folder to Add"
        });

        if (folders == null || folders.Count == 0) return;

        var folderPath = folders[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(folderPath))
            await vm.PlaylistViewModel.ProcessAndAddFilesAsync(new List<string> { folderPath }, vm.NoRecurse);
    }

    private void PlaylistDataGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is DataGrid dg &&
            dg.SelectedItem is JukeboxTrack track &&
            DataContext is JukeboxViewModel vm)
        {
            vm.CurrentTrack = track;
            if (vm.PlayCommand.CanExecute(null))
                vm.PlayCommand.Execute(null);
        }
    }
}