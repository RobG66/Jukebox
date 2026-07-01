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
    event EventHandler<double>? DurationChanged;
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
