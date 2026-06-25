using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Jukebox.ViewModels;
using System;
using System.ComponentModel;
using System.Diagnostics;

namespace Jukebox.Views;

public partial class ContentView : UserControl
{
    private JukeboxViewModel? _currentViewModel;
    private MpvView? _mpvView;
    private JukeboxVisualizations.Controls.ProjectMControl? _projectMControl;
    private bool _hasAttachedProjectM;
    private bool _isInVideoMode;

    // ── Layout-transition suppression (window resize only) ──
    private readonly Avalonia.Threading.DispatcherTimer _layoutSettleTimer;
    private bool _isSuppressingNativeRender;

    public ContentView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;

        _layoutSettleTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _layoutSettleTimer.Tick += OnLayoutSettleTimerTick;
    }

    /// <summary>
    /// Called by JukeboxControl when the host window is being resized.
    /// </summary>
    public void NotifyWindowResizing()
    {
        SuppressNativeRenderDuringLayoutTransition();
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
        _hasAttachedProjectM = false;

        if (_currentViewModel != null)
        {
            _currentViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _currentViewModel.VisualizerViewModel.PropertyChanged += OnVisualizerPropertyChanged;
            _currentViewModel.PcmDataAvailable += OnPcmDataAvailable;

            CheckAndAttachNativeControls();
            UpdateMediaHost();
        }
    }

    /// <summary>
    /// Detach the media host content immediately. Called during window
    /// close to ensure the MpvView (OpenGlControlBase) is removed from
    /// the visual tree BEFORE MpvContext is disposed — prevents
    /// AccessViolationException from the render callback firing on a
    /// freed render context.
    /// </summary>
    public void DetachMediaHost()
    {
        // Setting Content = null removes the active OpenGlControlBase
        // from the visual tree, triggering OnOpenGlDeinit and context
        // cleanup. This must happen before MpvContext.Dispose().
        MediaHost.Content = null;
        _isInVideoMode = false;
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _layoutSettleTimer.Stop();
        _isSuppressingNativeRender = false;

        if (_currentViewModel != null)
        {
            _currentViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _currentViewModel.VisualizerViewModel.PropertyChanged -= OnVisualizerPropertyChanged;
            _currentViewModel.PcmDataAvailable -= OnPcmDataAvailable;
            _currentViewModel = null;
        }

        // Clear the MediaHost — removes the active OpenGlControlBase from
        // the visual tree, which triggers its OnOpenGlDeinit / DoCleanup.
        MediaHost.Content = null;

        if (_projectMControl != null)
        {
            if (_projectMControl is IDisposable disposableProjectM)
            {
                try { disposableProjectM.Dispose(); }
                catch (Exception ex) { Trace.WriteLine($"[ContentView] ProjectMControl dispose failed: {ex.Message}"); }
            }
            _projectMControl = null;
            _hasAttachedProjectM = false;
        }

        // MpvView doesn't need explicit disposal — its render context is
        // owned by MpvContext (the VM), which disposes it separately.
        _mpvView = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JukeboxViewModel.IsBackendReady))
        {
            CheckAndAttachNativeControls();
            UpdateMediaHost();
        }
        else if (e.PropertyName == nameof(JukeboxViewModel.IsVisualizerVisible))
        {
            // Mode changed (audio ↔ video) — swap the MediaHost content.
            UpdateMediaHost();
        }
    }

    /// <summary>
    /// Swap the MediaHost between MpvView (video mode) and ProjectMControl
    /// (audio mode). Only ONE OpenGlControlBase is in the visual tree at a
    /// time — prevents GL context conflicts.
    /// </summary>
    private void UpdateMediaHost()
    {
        if (_currentViewModel == null || !_currentViewModel.IsBackendReady) return;

        bool wantVideo = !_currentViewModel.IsVisualizerVisible;

        if (wantVideo == _isInVideoMode) return; // no change

        // Remove the current content — this detaches the OpenGlControlBase
        // from the visual tree, triggering its cleanup (OnOpenGlDeinit,
        // context disposal).
        MediaHost.Content = null;

        if (wantVideo)
        {
            // ── Video mode: show MpvView ──
            if (_mpvView == null)
            {
                _mpvView = new MpvView();
                _mpvView[!MpvView.MpvContextProperty] = new Binding(nameof(JukeboxViewModel.MpvContext));
            }
            MediaHost.Content = _mpvView;
        }
        else
        {
            // ── Audio mode: show ProjectMControl ──
            if (_projectMControl == null)
            {
                EnsureProjectMAttached();
            }
            if (_projectMControl != null)
                MediaHost.Content = _projectMControl;
        }

        _isInVideoMode = wantVideo;
    }

    private void SuppressNativeRenderDuringLayoutTransition()
    {
        if (!_isSuppressingNativeRender)
        {
            _isSuppressingNativeRender = true;
            // Hide the active control during resize to prevent per-frame
            // GL surface resizes (stutter).
            if (_projectMControl != null && _currentViewModel is { IsVisualizerVisible: true })
            {
                _projectMControl.IsVisible = false;
            }
        }

        _layoutSettleTimer.Stop();
        _layoutSettleTimer.Start();
    }

    private void OnLayoutSettleTimerTick(object? sender, EventArgs e)
    {
        _layoutSettleTimer.Stop();
        _isSuppressingNativeRender = false;

        if (_projectMControl != null && _currentViewModel is { IsVisualizerVisible: true })
        {
            _projectMControl.IsVisible = true;
        }
    }

    /// <summary>
    /// Attach the ProjectMControl (lazy creation + engine start).
    /// </summary>
    private void CheckAndAttachNativeControls()
    {
        if (_currentViewModel == null || !_currentViewModel.IsBackendReady) return;

        if (_currentViewModel.IsBassAvailable && !_hasAttachedProjectM)
        {
            EnsureProjectMAttached();
        }
    }

    private void EnsureProjectMAttached()
    {
        if (_currentViewModel == null || _hasAttachedProjectM) return;

        _hasAttachedProjectM = true;
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [ContentView] Attaching ProjectM dynamically...");
        _projectMControl = new JukeboxVisualizations.Controls.ProjectMControl();
        _projectMControl[!JukeboxVisualizations.Controls.ProjectMControl.PresetPathProperty] =
            new Binding($"{nameof(JukeboxViewModel.VisualizerViewModel)}.{nameof(JukeboxVisualizerViewModel.SelectedVisualizerPath)}");

        var projectMPath = Jukebox.Services.PathProvider.Current.ProjectMPresetsDirectory;
        if (System.IO.Directory.Exists(projectMPath))
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [ContentView] Calling ProjectM StartEngine...");
            _projectMControl.StartEngine();

            var currentPreset = _currentViewModel.VisualizerViewModel.SelectedVisualizerPath;
            if (!string.IsNullOrEmpty(currentPreset))
            {
                _projectMControl.LoadPreset(currentPreset);
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
