using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Jukebox.Services;
using Jukebox.ViewModels;
using System;
using System.ComponentModel;
using System.Diagnostics;

namespace Jukebox.Views;

public partial class ContentView : UserControl
{
    private JukeboxViewModel? _currentViewModel;
    private MpvView? _mpvView;

    /// <summary>
    /// The optional visualizer control, created lazily through the active
    /// visualizer plugin contract.
    /// </summary>
    private Control? _visualizerControl;
    private bool _hasAttachedVisualizer;

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
        _hasAttachedVisualizer = false;

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

        if (_visualizerControl != null)
        {
            var active = _currentViewModel?.ActiveVisualizer;
            if (active != null)
            {
                active.TryDispose(_visualizerControl);
            }
            else if (_visualizerControl is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { }
            }
            _visualizerControl = null;
            _hasAttachedVisualizer = false;
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
        else if (e.PropertyName == nameof(JukeboxViewModel.IsVisualizerAvailable))
        {
            // Visualizer availability changed. Re-evaluate the media host contents.
            CheckAndAttachNativeControls();
            UpdateMediaHost();
        }
        else if (e.PropertyName == nameof(JukeboxViewModel.IsVisualizerEnabled))
        {
            UpdateMediaHost();
        }
    }

    /// <summary>
    /// Swap the MediaHost between MpvView (video mode), a plugin control
    /// (audio mode with visualizer available), and empty (audio mode with
    /// visualizer unavailable). Only ONE OpenGlControlBase is in the
    /// visual tree at a time — prevents GL context conflicts.
    ///
    /// When audio is playing but the visualizer is NOT available, the
    /// MediaHost is left empty (pure audio mode, no visualizer dependency).
    /// </summary>
    private void UpdateMediaHost()
    {
        if (_currentViewModel == null || !_currentViewModel.IsBackendReady) return;

        // wantVideo = !audio_mode (audio_mode is the existing semantic of
        // IsVisualizerVisible). Note this is independent of whether the
        // visualizer is available: when audio is playing without a viz,
        // wantVideo is still false, but we won't have a visualizer control
        // to show (MediaHost stays empty).
        bool wantVideo = !_currentViewModel.IsVisualizerVisible;
        bool showVisualizer = _currentViewModel.IsVisualizerVisible
                              && _currentViewModel.IsVisualizerAvailable
                              && _currentViewModel.IsVisualizerEnabled;

        // Compute the desired content for this state.
        Control? desiredContent;
        if (wantVideo)
            desiredContent = _mpvView;             // may be null if not yet created
        else if (showVisualizer)
            desiredContent = _visualizerControl;  // may be null if not yet created
        else
            desiredContent = null;                 // audio mode without viz

        // If MediaHost already holds the desired content, no swap needed.
        // (desiredContent can be null in two cases: (a) we want empty
        // MediaHost [audio mode without visualizer] — return if already
        // empty; (b) we want video/visualizer but the control hasn't
        // been created yet — fall through to create it.)
        if (ReferenceEquals(MediaHost.Content, desiredContent))
        {
            if (desiredContent != null) return;
            if (!wantVideo && !showVisualizer) return; // want empty, already empty
            // else: want video/viz but control not created → fall through
        }

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
        else if (showVisualizer)
        {
            // ── Audio mode + visualizer available: show plugin control ──
            if (_visualizerControl == null)
            {
                EnsureVisualizerAttached();
            }
            if (_visualizerControl != null)
                MediaHost.Content = _visualizerControl;
        }
        // else: audio mode + visualizer unavailable → MediaHost is left
        // empty (set to null above). BASS still plays audio normally;
        // the UI just shows the background (no OpenGlControlBase in the
        // visual tree).
    }

    private void SuppressNativeRenderDuringLayoutTransition()
    {
        if (!_isSuppressingNativeRender)
        {
            _isSuppressingNativeRender = true;
            // ProjectM benefits from being hidden during rapid resize. Do
            // not hide MpvView: making the video control invisible is itself
            // a guaranteed black flash, and libmpv supports target-size
            // changes on each render call.
            if (_visualizerControl != null
                && _currentViewModel is { IsVisualizerVisible: true, IsVisualizerAvailable: true })
            {
                _visualizerControl.IsVisible = false;
            }
        }

        _layoutSettleTimer.Stop();
        _layoutSettleTimer.Start();
    }

    private void OnLayoutSettleTimerTick(object? sender, EventArgs e)
    {
        _layoutSettleTimer.Stop();
        _isSuppressingNativeRender = false;

        if (_visualizerControl != null
            && _currentViewModel is { IsVisualizerVisible: true, IsVisualizerAvailable: true })
        {
            _visualizerControl.IsVisible = true;
        }
    }

    /// <summary>
    /// Attach the plugin control (lazy creation + engine start). Only
    /// called when the visualizer runtime reports availability.
    /// </summary>
    private void CheckAndAttachNativeControls()
    {
        if (_currentViewModel == null || !_currentViewModel.IsBackendReady) return;

        if (_currentViewModel.IsBassAvailable
            && _currentViewModel.IsVisualizerAvailable
            && !_hasAttachedVisualizer)
        {
            EnsureVisualizerAttached();
        }
    }

    private void EnsureVisualizerAttached()
    {
        if (_currentViewModel == null || _hasAttachedVisualizer) return;

        var active = _currentViewModel.ActiveVisualizer;
        if (active == null || !active.IsAvailable) return;

        _hasAttachedVisualizer = true;
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [ContentView] Attaching visualizer plugin...");

        _visualizerControl = active.CreateControl();
        if (_visualizerControl == null)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [ContentView] Visualizer control creation returned null — visualizer disabled.");
            return;
        }

        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [ContentView] Starting visualizer engine...");
        active.StartEngine(_visualizerControl);

        var currentPreset = _currentViewModel.VisualizerViewModel.SelectedVisualizerPath;
        if (!string.IsNullOrEmpty(currentPreset))
        {
            active.LoadPreset(_visualizerControl, currentPreset);
        }
    }

    private void OnVisualizerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(JukeboxVisualizerViewModel.SelectedVisualizerPath)) return;

        var path = _currentViewModel?.VisualizerViewModel.SelectedVisualizerPath;
        if (!string.IsNullOrEmpty(path) && _visualizerControl != null && _currentViewModel != null)
        {
            _currentViewModel.ActiveVisualizer?.LoadPreset(_visualizerControl, path);
        }
    }

    private void OnPcmDataAvailable(object? sender, short[] e)
    {
        if (_visualizerControl != null && _currentViewModel != null)
        {
            _currentViewModel.ActiveVisualizer?.FeedPcm(_visualizerControl, e);
        }
    }
}
