using Jukebox.Models;
using Jukebox.Mpv;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Threading;

namespace Jukebox.Services;

public sealed class MpvPlaybackEngine : IMediaPlayerEngine
{
    #region Fields & Constants
    private MpvContext? _mpv;
    private double _volume = 100;

    // One-shot guard for PlaybackStarted — reset in Stop and at the start
    // of PlayAsync, set to true when MPV reports that the file has loaded
    // (or when a positive time-pos is observed as a fallback). Volatile
    // because the MPV event thread writes
    // it while Stop/PlayAsync on the UI thread read/reset it.
    private volatile bool _playbackStartedFired;
    #endregion

    #region Public Properties
    public bool IsAvailable { get; private set; }
    public MpvContext? MpvContext => _mpv;
    #endregion

    #region Public Events
    public event EventHandler? PlaybackEnded;
    public event EventHandler? PlaybackStarted;
    public event EventHandler<double>? DurationChanged;
#pragma warning disable 0067
    public event EventHandler<string>? MetadataChanged;
#pragma warning restore 0067
    #endregion

    #region Constructor
    public MpvPlaybackEngine()
    {
    }
    #endregion

    #region Public Methods
    public void Initialize()
    {
        var sw = Stopwatch.StartNew();
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MPV Engine] Initializing MPV...");
        try
        {
            _mpv = new MpvContext();

            if (!_mpv.Initialize())
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MPV Engine] MPV initialization failed — libmpv not found?");
                IsAvailable = false;
                return;
            }

            _mpv.ObserveProperty("time-pos", MpvFormat.Double);
            _mpv.ObserveProperty("duration", MpvFormat.Double);
            _mpv.ObserveProperty("eof-reached", MpvFormat.Flag);

            _mpv.PropertyChanged += OnMpvPropertyChanged;
            _mpv.FileLoaded += OnMpvFileLoaded;
            _mpv.EndReached += OnMpvEndReached;

            IsAvailable = true;
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MPV Engine] MPV initialized successfully in {sw.ElapsedMilliseconds}ms.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [MPV Engine] MPV Init Exception: {ex.Message}");
            IsAvailable = false;
        }
    }

    public Task PlayAsync(JukeboxTrack track) => PlayAsync(track, CancellationToken.None);

    public async Task PlayAsync(JukeboxTrack track, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable || _mpv == null)
        {
            throw new InvalidOperationException("Video playback is unavailable. MPV (libmpv) failed to initialize. " +
                "Make sure libmpv-2.dll (Windows) or libmpv.so.2 (Linux) is in the " +
                "lib/ folder next to Jukebox.exe.");
        }

        // Reset the one-shot PlaybackStarted guard before loading a new file
        // so the next positive time-pos report is treated as the start of
        // this track (not a leftover from the previous one).
        _playbackStartedFired = false;

        Stop();

        await _mpv.WaitForRenderContextReadyAsync(3000, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        _mpv.LoadFile(track.FilePath);
        _mpv.SetVolume(_volume);
        _mpv.Play();
    }

    public void Pause()
    {
        _mpv?.Pause();
    }

    public void Stop()
    {
        // Reset the one-shot PlaybackStarted guard so the next PlayAsync can
        // re-fire it when MPV reports a fresh positive time-pos.
        _playbackStartedFired = false;
        _mpv?.Stop();
    }

    public void Resume()
    {
        _mpv?.Play();
    }

    public void Seek(double positionMs)
    {
        _mpv?.SeekAbsolute(positionMs / 1000.0);
    }

    public double GetPositionMs()
    {
        var pos = _mpv?.GetPosition();
        return pos.HasValue ? pos.Value * 1000 : -1;
    }

    public void SetVolume(double volume)
    {
        _volume = volume;
        _mpv?.SetVolume(volume);
    }
    #endregion

    #region Private Methods
    private void OnMpvPropertyChanged(string name, object? value)
    {
        if (name == "duration" && value is double dur)
        {
            DurationChanged?.Invoke(this, dur * 1000.0);
        }
        else if (name == "time-pos" && value is double pos && pos > 0)
        {
            // First positive time-pos => MPV is actively decoding & playing
            // back this file. One-shot per PlayAsync via _playbackStartedFired.
            // Raised on the MPV event thread — handlers must marshal to UI
            // thread if they touch UI. JukeboxViewModel's handler does so.
            SignalPlaybackStarted();
        }
    }

    private void OnMpvFileLoaded()
    {
        string hwdec = _mpv?.GetString("hwdec-current") ?? "none";
        string decoder = _mpv?.GetString("video-codec") ?? "unknown";
        Debug.WriteLine($"[MPV Engine] File loaded. decoder={decoder}, hwdec-current={hwdec}.");
        SignalPlaybackStarted();
    }

    private void SignalPlaybackStarted()
    {
        if (_playbackStartedFired) return;
        _playbackStartedFired = true;
        PlaybackStarted?.Invoke(this, EventArgs.Empty);
    }

    private void OnMpvEndReached()
    {
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }
    #endregion

    #region Dispose
    // MpvContext teardown remains synchronous so its native operations finish
    // before this method returns. The owning view-model invokes engine disposal
    // on a worker thread and awaits it with the window's shutdown timeout.
    public void Dispose()
    {
        var mpv = _mpv;
        _mpv = null;

        // Dispose any context that was created, including a partially initialized
        // one. MpvContext.Dispose is idempotent and safely ignores zero handles.
        if (mpv != null)
        {
            mpv.PropertyChanged -= OnMpvPropertyChanged;
            mpv.FileLoaded -= OnMpvFileLoaded;
            mpv.EndReached -= OnMpvEndReached;
            try { mpv.Dispose(); }
            catch (Exception ex) { Debug.WriteLine($"[MPV Engine] Dispose failed: {ex.Message}"); }
        }
        IsAvailable = false;
    }
    #endregion
}
