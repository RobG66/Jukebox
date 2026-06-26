using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Jukebox.Extensions;
using Jukebox.Mpv;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Jukebox.ViewModels;

public partial class JukeboxViewModel
{
    #region Fields
    /// <summary>
    /// The MPV player context. Handles all video playback via libmpv.
    /// Renders into Avalonia's OpenGL context via MpvView (OpenGlControlBase).
    /// No native HWND — no airspace issue.
    /// </summary>
    private MpvContext? _mpv;

    /// <summary>
    /// The MPV context for video playback. The MpvView in ContentView binds
    /// to this to connect the render context.
    /// </summary>
    public MpvContext? MpvContext => _mpv;
    #endregion

    #region Observable Properties
    [ObservableProperty] private bool _isMpvAvailable;
    #endregion

    #region Initialization
    private void InitializeMpv()
    {
        var sw = Stopwatch.StartNew();
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] Initializing MPV...");
        try
        {
            _mpv = new MpvContext();

            if (!_mpv.Initialize())
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] MPV initialization failed — libmpv not found?");
                Dispatcher.UIThread.Post(() => IsMpvAvailable = false);
                return;
            }

            // Observe properties for UI binding. These fire on a background
            // thread; we marshal to the UI thread in the callbacks.
            _mpv.ObserveProperty("time-pos", MpvFormat.Double);
            _mpv.ObserveProperty("duration", MpvFormat.Double);
            _mpv.ObserveProperty("eof-reached", MpvFormat.Flag);

            _mpv.PropertyChanged += OnMpvPropertyChanged;
            _mpv.EndReached += OnMpvEndReached;

            Dispatcher.UIThread.Post(() => IsMpvAvailable = true);
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] MPV initialized successfully in {sw.ElapsedMilliseconds}ms.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] MPV Init Exception: {ex.Message}");
            Dispatcher.UIThread.Post(() => IsMpvAvailable = false);
        }
    }
    #endregion

    #region Playback
    private async Task PlayVideoAsync()
    {
        if (!IsMpvAvailable || _mpv == null)
        {
            await Jukebox.Views.ThreeButtonDialogView.ShowErrorAsync(
                "Video Error",
                "Video playback is unavailable. MPV (libmpv) failed to initialize. " +
                "Make sure libmpv-2.dll (Windows) or libmpv.so.2 (Linux) is in the " +
                "lib/ folder next to Jukebox.exe.");
            return;
        }

        if (CurrentTrack == null) return;

        // ── Fix for "first video is black" race condition ──
        // Wait for the render context to be ready before loading the file.
        // If we call LoadFile before the render surface exists, MPV starts
        // decoding with no output target and produces a black screen.
        // See: https://github.com/damontecres/Wholphin/issues/576
        await _mpv.WaitForRenderContextReadyAsync();

        // Load the file — "replace" stops any current playback.
        _mpv.LoadFile(CurrentTrack.FilePath);

        // Set volume (MPV uses 0-100, same as our VM).
        _mpv.SetVolume(Volume);

        // Ensure not paused.
        _mpv.Play();

        SetPlayingState();
    }

    private void ResumeMpv()
    {
        _mpv?.Play();
    }

    private void PauseMpv()
    {
        _mpv?.Pause();
    }

    private void StopMpv()
    {
        _mpv?.Stop();
    }

    private void SeekMpv(double positionMs)
    {
        // MPV seek takes seconds.
        _mpv?.SeekAbsolute(positionMs / 1000.0);
    }

    private double GetMpvPositionMs()
    {
        var pos = _mpv?.GetPosition();
        return pos.HasValue ? pos.Value * 1000 : -1;
    }

    private void ApplyMpvVolume(double volume)
    {
        _mpv?.SetVolume(volume);
    }
    #endregion

    #region Callbacks
    private void OnMpvPropertyChanged(string name, object? value)
    {
        // Called on a background thread — marshal to UI thread.
        Dispatcher.UIThread.Post(() =>
        {
            if (name == "time-pos" && value is double pos)
            {
                var posMs = pos * 1000;
                if (!_isTimerUpdating && pos >= 0)
                {
                    PlaybackPosition = posMs;
                    CurrentTimeString = TimeSpan.FromMilliseconds(posMs).ToString(@"m\:ss");
                }
            }
            else if (name == "duration" && value is double dur)
            {
                var durMs = dur * 1000;
                UpdateTrackDuration(durMs);
                if (CurrentTrack != null && CurrentTrack.Length == TimeSpan.Zero)
                    CurrentTrack.Length = TimeSpan.FromMilliseconds(durMs);
            }
        });
    }

    private void OnMpvEndReached()
    {
        // Called on a background thread — marshal to UI thread.
        Dispatcher.UIThread.Post(() =>
        {
            if (IsRepeatEnabled)
            {
                StartTrackAsync().SafeFireAndForget(nameof(StartTrackAsync));
                return;
            }

            var next = PickNextTrack(IsRandomPlayback);
            if (next != null)
            {
                CurrentTrack = next;
                StartTrackAsync().SafeFireAndForget(nameof(StartTrackAsync));
                return;
            }

            _playbackTimer?.Stop();
            StopMpv();
            CanPlay = PlaylistViewModel.Playlist.Count > 0;
            CanPause = false;
            CanStop = false;
            CurrentTimeString = "0:00";
            PlaybackPosition = 0;
        });
    }
    #endregion

    #region Dispose
    private Task DisposeMpvAsync()
    {
        var mpv = _mpv;
        _mpv = null;

        return Task.Run(() =>
        {
            if (mpv != null)
            {
                try { mpv.Dispose(); }
                catch (Exception ex) { Debug.WriteLine($"[MPV] Dispose failed: {ex.Message}"); }
            }
        });
    }
    #endregion
}
