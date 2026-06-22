using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Jukebox.ViewModels;
using System;
using System.ComponentModel;

namespace Jukebox.Views;

public partial class ContentView : UserControl
{
    private JukeboxViewModel? _currentViewModel;
    private JukeboxVisualizations.Controls.ProjectMControl? _projectMControl;
    private bool _hasAttachedVideo;
    private bool _hasAttachedProjectM;

    public ContentView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_currentViewModel != null)
        {
            _currentViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _currentViewModel.VisualizerViewModel.PropertyChanged -= OnVisualizerPropertyChanged;
            _currentViewModel.PcmDataAvailable -= OnPcmDataAvailable;
        }

        _currentViewModel = DataContext as JukeboxViewModel;
        _hasAttachedVideo = false;
        _hasAttachedProjectM = false;

        if (_currentViewModel != null)
        {
            _currentViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _currentViewModel.VisualizerViewModel.PropertyChanged += OnVisualizerPropertyChanged;
            _currentViewModel.PcmDataAvailable += OnPcmDataAvailable;
            
            // Check immediately in case they are already available
            CheckAndAttachNativeControls();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JukeboxViewModel.IsBackendReady))
        {
            CheckAndAttachNativeControls();
        }
    }

    private void CheckAndAttachNativeControls()
    {
        if (_currentViewModel == null || !_currentViewModel.IsBackendReady) return;

        // Attach VLC VideoView
        if (_currentViewModel.IsVlcAvailable && !_hasAttachedVideo)
        {
            _hasAttachedVideo = true;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [ContentView] Attaching VideoView dynamically...");
            var videoView = new LibVLCSharp.Avalonia.VideoView();
            videoView[!LibVLCSharp.Avalonia.VideoView.MediaPlayerProperty] = new Binding(nameof(JukeboxViewModel.MediaPlayer));
            VideoHost.Child = videoView;
        }

        // Attach ProjectMControl
        if (_currentViewModel.IsBassAvailable && !_hasAttachedProjectM)
        {
            _hasAttachedProjectM = true;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [ContentView] Attaching ProjectM dynamically...");
            _projectMControl = new JukeboxVisualizations.Controls.ProjectMControl();
            _projectMControl[!JukeboxVisualizations.Controls.ProjectMControl.PresetPathProperty] = new Binding("VisualizerViewModel.SelectedVisualizerPath");
            ProjectMHost.Child = _projectMControl;

            var projectMPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectM", "Presets");
            if (System.IO.Directory.Exists(projectMPath))
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [ContentView] Calling ProjectM StartEngine...");
                _projectMControl.StartEngine();

                var currentPreset = _currentViewModel.VisualizerViewModel.SelectedVisualizerPath;
                if (!string.IsNullOrEmpty(currentPreset))
                {
                    _projectMControl.LoadPreset(currentPreset);
                }
            }
        }
    }

    private void OnVisualizerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(JukeboxVisualizerViewModel.SelectedVisualizerPath)) return;

        var path = _currentViewModel?.VisualizerViewModel.SelectedVisualizerPath;
        if (!string.IsNullOrEmpty(path) && _projectMControl != null)
        {
            _projectMControl.LoadPreset(path);
        }
    }

    private void OnPcmDataAvailable(object? sender, short[] e)
    {
        _projectMControl?.FeedPcm(e);
    }
}
