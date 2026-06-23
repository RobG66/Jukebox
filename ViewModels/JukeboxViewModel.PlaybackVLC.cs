using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LibVLCSharp.Shared;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Jukebox.ViewModels;

public partial class JukeboxViewModel
{
    #region Fields
    private LibVLC? _libVLC;
    private bool _ownsLibVLC;
    private Task _mediaPlayerDisposalTask = Task.CompletedTask;
    #endregion

    #region Observable Properties
    [ObservableProperty] private MediaPlayer? _mediaPlayer;
    [ObservableProperty] private bool _isVlcAvailable;
    #endregion

    #region Initialization
    private void InitializeVlc()
    {
        var sw = Stopwatch.StartNew();
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] Initializing LibVLC...");
        try
        {
            if (SharedLibVLC != null)
            {
                _libVLC = SharedLibVLC;
                _ownsLibVLC = false;
                Dispatcher.UIThread.Post(() => IsVlcAvailable = true);
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] Using shared LibVLC instance.");
            }
            else
            {
                Core.Initialize();
                _libVLC = new LibVLC();
                _ownsLibVLC = true;
                Dispatcher.UIThread.Post(() => IsVlcAvailable = true);
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] LibVLC initialized successfully in {sw.ElapsedMilliseconds}ms.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INIT] LibVLC Init Exception: {ex.Message}");
        }
    }
    #endregion

    #region Playback
    private async Task PlayVideoAsync()
    {
        if (!IsVlcAvailable)
        {
            await Jukebox.Views.ThreeButtonDialogView.ShowErrorAsync(
                "Video Error",
                "Video playback is unavailable. LibVLC failed to initialize.");
            return;
        }

        if (CurrentTrack == null || _libVLC == null) return;

        if (MediaPlayer == null)
        {
            MediaPlayer = new MediaPlayer(_libVLC);
            MediaPlayer.EndReached += OnMediaPlayerEndReached;
            MediaPlayer.LengthChanged += OnMediaPlayerLengthChanged;
        }

        var oldMedia = MediaPlayer.Media;
        var media = new Media(_libVLC, CurrentTrack.FilePath);
        MediaPlayer.Media = media;
        oldMedia?.Dispose();

        MediaPlayer.Volume = (int)Volume;
        MediaPlayer.Play();

        SetPlayingState();
    }

    private void ResumeVlc()
    {
        MediaPlayer?.Play();
    }

    private void PauseVlc()
    {
        MediaPlayer?.Pause();
    }

    private void StopVlc(bool skipStop = false)
    {
        if (MediaPlayer != null)
        {
            var media = MediaPlayer.Media;
            if (!skipStop) try { MediaPlayer.Stop(); } catch { }
            try { MediaPlayer.Media = null; } catch { }
            try { media?.Dispose(); } catch { }
        }
    }

    private void SeekVlc(double positionMs)
    {
        if (MediaPlayer != null) MediaPlayer.Time = (long)positionMs;
    }

    private double GetVlcPositionMs()
    {
        return MediaPlayer?.Time ?? -1;
    }

    private void ApplyVlcVolume(double volume)
    {
        if (MediaPlayer != null) MediaPlayer.Volume = (int)volume;
    }
    #endregion

    #region Callbacks
    private void OnMediaPlayerEndReached(object? sender, EventArgs e)
    {
        _ = Task.Run(async () =>
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (IsRepeatEnabled)
                {
                    await StartTrackAsync(vlcEndReached: true);
                    return;
                }

                var next = PickNextTrack(IsRandomPlayback);
                if (next != null)
                {
                    CurrentTrack = next;
                    await StartTrackAsync(vlcEndReached: true);
                    return;
                }

                _playbackTimer?.Stop();
                StopVlc(skipStop: true);
                CanPlay = PlaylistViewModel.Playlist.Count > 0;
                CanPause = false;
                CanStop = false;
                CurrentTimeString = "0:00";
                PlaybackPosition = 0;
            });
        });
    }

    private void OnMediaPlayerLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateTrackDuration(e.Length);
            if (CurrentTrack != null && CurrentTrack.Length == TimeSpan.Zero)
                CurrentTrack.Length = TimeSpan.FromMilliseconds(e.Length);
        });
    }
    #endregion

    #region Dispose
    private async Task DisposeVlcAsync()
    {
        var player = MediaPlayer;
        var libVlc = _libVLC;
        var ownsLibVlc = _ownsLibVLC;

        MediaPlayer = null;
        _libVLC = null;

        if (player != null)
        {
            player.EndReached -= OnMediaPlayerEndReached;
            player.LengthChanged -= OnMediaPlayerLengthChanged;
        }

        var previousTask = _mediaPlayerDisposalTask;
        _mediaPlayerDisposalTask = Task.Run(async () =>
        {
            try { await previousTask; } catch { }
            if (player != null)
            {
                try { player.Stop(); } catch { }
                try { player.Media?.Dispose(); } catch { }
                try { player.Dispose(); } catch { }
            }
            if (libVlc != null && ownsLibVlc)
            {
                try { libVlc.Dispose(); } catch { }
            }
        });

        await _mediaPlayerDisposalTask;
    }
    #endregion
}
