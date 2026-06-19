using Avalonia.Controls;
using Avalonia.Interactivity;
using Jukebox.ViewModels;
using System;
using System.ComponentModel;

namespace Jukebox.Views;

public partial class ContentView : UserControl
{
    private JukeboxViewModel? _currentViewModel;

    public ContentView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ProjectMDisplay.StartEngine();
    }

    // -------------------------------------------------------------------------
    // DataContext wiring — PCM feed and visualizer preset loading
    // -------------------------------------------------------------------------
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_currentViewModel != null)
        {
            _currentViewModel.PcmDataAvailable -= OnPcmDataAvailable;
            _currentViewModel.VisualizerViewModel.PropertyChanged -= OnVisualizerPropertyChanged;
        }

        _currentViewModel = DataContext as JukeboxViewModel;

        if (_currentViewModel != null)
        {
            _currentViewModel.PcmDataAvailable += OnPcmDataAvailable;
            _currentViewModel.VisualizerViewModel.PropertyChanged += OnVisualizerPropertyChanged;
        }
    }

    private void OnPcmDataAvailable(object? sender, short[] buffer)
        => ProjectMDisplay.FeedPcm(buffer);

    private void OnVisualizerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(JukeboxVisualizerViewModel.SelectedVisualizerPath)) return;

        var path = _currentViewModel?.VisualizerViewModel.SelectedVisualizerPath;
        if (!string.IsNullOrEmpty(path))
            ProjectMDisplay.LoadPreset(path);
    }
}
