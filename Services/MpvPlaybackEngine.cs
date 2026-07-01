using Avalonia.Threading;
using Jukebox.Models;
using Jukebox.Mpv;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Jukebox.Services;

public sealed class MpvPlaybackEngine : IMediaPlayerEngine
{
    #region Fields & Constants
    private MpvContext? _mpv;
    private double _volume = 100;
    #endregion

    #region Public Properties
    public bool IsAvailable { get; private set; }
    public MpvContext? MpvContext => _mpv;
    #endregion

    #region Public Events
    public event EventHandler? PlaybackEnded;
    public event EventHandler<double>? DurationChanged;
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

    public async Task PlayAsync(JukeboxTrack track)
    {
        if (!IsAvailable || _mpv == null)
        {
            await Jukebox.Views.ThreeButtonDialogView.ShowErrorAsync(
                "Video Error",
                "Video playback is unavailable. MPV (libmpv) failed to initialize. " +
                "Make sure libmpv-2.dll (Windows) or libmpv.so.2 (Linux) is in the " +
                "lib/ folder next to Jukebox.exe.");
            return;
        }

        Stop();

        await _mpv.WaitForRenderContextReadyAsync();

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
    }

    private void OnMpvEndReached()
    {
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }
    #endregion

    #region Dispose
    public void Dispose()
    {
        var mpv = _mpv;
        _mpv = null;

        if (mpv != null)
        {
            mpv.PropertyChanged -= OnMpvPropertyChanged;
            mpv.EndReached -= OnMpvEndReached;
            Task.Run(() =>
            {
                try { mpv.Dispose(); }
                catch (Exception ex) { Debug.WriteLine($"[MPV Engine] Dispose failed: {ex.Message}"); }
            });
        }
        IsAvailable = false;
    }
    #endregion
}
