namespace Jukebox.Services;

/// <summary>
/// Provides canonical filesystem paths for the Jukebox application.
/// Single point of change for all path-related decisions — addresses
/// Smell Test Report §6.3 (5 occurrences of duplicated
/// Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ...) calls)
/// and §7.2 item #7.
///
/// The default implementation is <see cref="PathProvider"/> (singleton via
/// <see cref="PathProvider.Current"/>). To override paths in tests,
/// call <see cref="PathProvider.Override(IPathProvider)"/> with a stub.
/// </summary>
public interface IPathProvider
{
    /// <summary>
    /// Directory containing the host playback runtime libraries (BASS,
    /// libmpv, and libvgm). Plugin-owned native libraries are not loaded from here.
    /// </summary>
    string NativeLibDirectory { get; }



    /// <summary>
    /// Directory for miscellaneous settings files. App-relative, like everything else here —
    /// see <see cref="PathProvider.AppBaseDirectory"/>.
    /// </summary>
    string SettingsDirectory { get; }

    /// <summary>Full path to the EQ settings JSON file.</summary>
    string EqSettingsFile { get; }

    /// <summary>Directory containing host-owned saved playlists.</summary>
    string PlaylistsDirectory { get; }

    /// <summary>Directory for host and plugin cached data.</summary>
    string CacheDirectory { get; }
}
