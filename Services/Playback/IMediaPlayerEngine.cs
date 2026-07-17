using System;
using System.Threading.Tasks;
using Jukebox.Models;

namespace Jukebox.Services;

public interface IMediaPlayerEngine : IDisposable
{
    #region Properties
    bool IsAvailable { get; }
    #endregion

    #region Events
    event EventHandler? PlaybackEnded;
    /// <summary>
    /// Raised the first time the engine actually starts producing audio
    /// (BASS/VGM: first PCM buffer; MPV: first non-zero time-pos). Used by
    /// the playback supervisor to clear the "Connecting..." overlay and to
    /// gate the stream-connection timeout — if this doesn't fire within
    /// <see cref="Jukebox.Constants.StreamConnectionTimeoutMs"/> for a URL
    /// stream, the attempt is treated as failed and aborted.
    /// </summary>
    event EventHandler? PlaybackStarted;
    event EventHandler<double>? DurationChanged;
    event EventHandler<string>? MetadataChanged;
    #endregion

    #region Public Methods
    Task PlayAsync(JukeboxTrack track);
    void Pause();
    void Stop();
    void Resume();
    void Seek(double positionMs);
    double GetPositionMs();
    void SetVolume(double volume);
    #endregion
}
