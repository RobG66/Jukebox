using Jukebox.Plugin.Abstractions;
using Jukebox.Models;
using Jukebox.Services;
using Jukebox.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Jukebox.Services;

/// <summary>
/// Concrete <see cref="IJukeboxMediaBrowserContext"/> and
/// <see cref="IJukeboxMediaBrowserContextFactory"/>. Lives in the main
/// app so it can reference <see cref="JukeboxPlaylistViewModel"/> and
/// <see cref="PathProvider"/> — neither of which are visible to plugins.
///
/// Each plugin gets its own <c>Cache/Plugins/&lt;id&gt;/</c> directory
/// for its private data, created on demand.
/// </summary>
public sealed class JukeboxPluginContextFactory : IJukeboxMediaBrowserContextFactory
{
    private readonly JukeboxPlaylistViewModel _playlistViewModel;
    private readonly JukeboxViewModel _jukeboxViewModel;
    private readonly IUserDialogService _dialogService;

    public JukeboxPluginContextFactory(JukeboxPlaylistViewModel playlistViewModel, JukeboxViewModel jukeboxViewModel)
        : this(playlistViewModel, jukeboxViewModel, null) { }

    // Constructor for testability — tests can inject a stub dialog service.
    public JukeboxPluginContextFactory(JukeboxPlaylistViewModel playlistViewModel, JukeboxViewModel jukeboxViewModel, IUserDialogService? dialogService)
    {
        _playlistViewModel = playlistViewModel;
        _jukeboxViewModel = jukeboxViewModel;
        _dialogService = dialogService ?? new UserDialogService();
    }

    public IJukeboxMediaBrowserContext CreateContext(string pluginId)
    {
        var dataDir = Path.Combine(PathProvider.Current.CacheDirectory, "Plugins", pluginId);
        Directory.CreateDirectory(dataDir);
        return new JukeboxPluginContext(pluginId, _playlistViewModel, _jukeboxViewModel, dataDir, _dialogService);
    }

    private sealed class JukeboxPluginContext : IJukeboxMediaBrowserContext
    {
        private readonly string _pluginId;
        private readonly JukeboxPlaylistViewModel _playlistViewModel;
        private readonly JukeboxViewModel _jukeboxViewModel;
        private readonly IUserDialogService _dialogService;

        public JukeboxPluginContext(
            string pluginId,
            JukeboxPlaylistViewModel playlistViewModel,
            JukeboxViewModel jukeboxViewModel,
            string dataDir,
            IUserDialogService dialogService)
        {
            _pluginId = pluginId;
            _playlistViewModel = playlistViewModel;
            _jukeboxViewModel = jukeboxViewModel;
            _dialogService = dialogService;
            PluginDataDirectory = dataDir;

            _jukeboxViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(JukeboxViewModel.CurrentTrack))
                {
                    CurrentlyPlayingChanged?.Invoke();
                }
            };
        }

        public string PluginDataDirectory { get; }

        public void PlayNow(PlayRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var track = MapRequest(request);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // A plugin is a discovery surface, so playing one of its items
                // must not erase work the user already put in the host queue.
                // Inserting after the current row also lets natural playback
                // resume with the following queued item when this track ends.
                _playlistViewModel.InsertNextInPlayQueue(
                    new[] { track },
                    _jukeboxViewModel.CurrentTrack);

                if (_jukeboxViewModel.PlayTrackCommand.CanExecute(track))
                {
                    _jukeboxViewModel.PlayTrackCommand.Execute(track);
                }
            });
        }

        public void ReplaceQueueAndPlay(IEnumerable<PlayRequest> queue, string activeSource)
        {
            ArgumentNullException.ThrowIfNull(queue);

            var tracks = new List<JukeboxTrack>();
            JukeboxTrack? trackToPlay = null;

            foreach (var request in queue)
            {
                var track = MapRequest(request);
                tracks.Add(track);

                bool isRequestedTrack = string.Equals(
                    request.Url,
                    activeSource,
                    StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        request.SourceUrl,
                        activeSource,
                        StringComparison.OrdinalIgnoreCase);

                if (trackToPlay == null && isRequestedTrack)
                {
                    trackToPlay = track;
                }
            }

            trackToPlay ??= tracks.Count > 0 ? tracks[0] : null;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _playlistViewModel.ReplacePlayQueue(tracks);

                if (trackToPlay != null && _jukeboxViewModel.PlayTrackCommand.CanExecute(trackToPlay))
                {
                    _jukeboxViewModel.PlayTrackCommand.Execute(trackToPlay);
                }
            });
        }

        public void Log(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[Plugin] {message}");
        }

        public void AddToQueue(PlayRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var track = MapRequest(request);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _playlistViewModel.AppendToPlayQueue(new[] { track });
            });
        }

        public void AddRangeToQueue(IEnumerable<PlayRequest> requests)
        {
            ArgumentNullException.ThrowIfNull(requests);

            var tracks = new List<JukeboxTrack>();
            foreach (var request in requests)
            {
                tracks.Add(MapRequest(request));
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _playlistViewModel.AppendToPlayQueue(tracks);
            });
        }

        public void UpdateTrackUrl(string originalUrl, string resolvedUrl)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _playlistViewModel.UpdatePlayQueueUrl(originalUrl, resolvedUrl);
            });
        }

        public Task<bool> ShowConfirmAsync(string title, string message)
        {
            return _dialogService.ShowConfirmAsync(title, message);
        }

        public string? CurrentlyPlayingUrl => _jukeboxViewModel.CurrentTrack?.PlaybackSource;

        public string? CurrentlyPlayingTitle => _jukeboxViewModel.CurrentTrack?.DisplayName;

        public event Action? CurrentlyPlayingChanged;

        private JukeboxTrack MapRequest(PlayRequest request)
        {
            string bitrate = FormatBitrate(request);

            return new JukeboxTrack
            {
                DisplayName = string.IsNullOrWhiteSpace(request.Title)
                    ? "Unknown Track"
                    : request.Title,
                FilePath = request.Url,
                OriginalUrl = string.IsNullOrWhiteSpace(request.SourceUrl)
                    ? null
                    : request.SourceUrl,
                SourcePluginId = string.IsNullOrWhiteSpace(request.SourcePluginId)
                    ? _pluginId
                    : request.SourcePluginId,
                Length = request.Length is { } length && length > TimeSpan.Zero
                    ? length
                    : TimeSpan.Zero,
                Bitrate = bitrate,
                Genre = string.IsNullOrWhiteSpace(request.Genre) ? "—" : request.Genre,
                Country = string.IsNullOrWhiteSpace(request.Country) ? "—" : request.Country,
                Location = string.IsNullOrWhiteSpace(request.Location) ? "—" : request.Location,
                IsTagged = true
            };
        }

        private static string FormatBitrate(PlayRequest request)
        {
            bool hasBitrate = request.Bitrate is > 0;
            bool hasCodec = !string.IsNullOrWhiteSpace(request.Codec);

            if (hasBitrate && hasCodec)
            {
                return $"{request.Bitrate} kbps | {request.Codec}";
            }

            if (hasBitrate)
            {
                return $"{request.Bitrate} kbps";
            }

            return hasCodec ? request.Codec! : "—";
        }
    }
}
