using System.Collections.Generic;
using System.Linq;

namespace Jukebox;

public static class Constants
{
    public static readonly string[] AudioExtensions = { ".mp3", ".flac", ".wav", ".ogg", ".m4a", ".wma" };
    
    public static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".avi", ".webm" };
    
    public static readonly HashSet<string> SupportedMediaExtensions = 
        new HashSet<string>(AudioExtensions.Concat(VideoExtensions), System.StringComparer.OrdinalIgnoreCase);

    public const string SettingsDirectoryName = "Jukebox";
    public const string EqSettingsFileName = "EqSettings.json";
}
