using System;
using System.Linq;
using System.Threading.Tasks;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace Jukebox.Services;

public static class YouTubeResolver
{
    private static readonly YoutubeClient _client = new();

    public static bool IsYouTubeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        return url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<(string Title, TimeSpan Duration)> GetMetadataAsync(string url)
    {
        try
        {
            var video = await _client.Videos.GetAsync(url);
            return (video.Title, video.Duration ?? TimeSpan.Zero);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[YouTubeResolver] GetMetadataAsync failed: {ex.Message}");
            throw;
        }
    }

    public static async Task<string> ResolveAudioUrlAsync(string url)
    {
        try
        {
            var streamManifest = await _client.Videos.Streams.GetManifestAsync(url);
            
            // Prioritize progressive Muxed streams (video + audio in a standard monolithic MP4 container)
            // instead of fragmented/DASH AudioOnly streams (which standard BASS cannot decode
            // due to lack of segment/manifest parsing support). BASS will play the audio track
            // and ignore the video track natively.
            IStreamInfo? stream = streamManifest.GetMuxedStreams()
                .OrderByDescending(s => s.VideoQuality)
                .FirstOrDefault();

            if (stream == null)
            {
                stream = streamManifest.GetAudioOnlyStreams().GetWithHighestBitrate();
            }

            if (stream == null)
            {
                throw new InvalidOperationException("No suitable streams found for the YouTube video.");
            }
            return stream.Url;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[YouTubeResolver] ResolveAudioUrlAsync failed: {ex.Message}");
            throw;
        }
    }
}
