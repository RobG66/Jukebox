using System;
using System.IO;

namespace Jukebox.Services;

/// <summary>
/// Default <see cref="IPathProvider"/> implementation. Everything lives
/// under the application's own base directory — host native libraries
/// (<c>lib/</c>), optional plugins (<c>plugins/</c>), saved
/// playlists (<c>Playlists/</c>), cached data (<c>Cache/</c>), and
/// miscellaneous settings (<c>Settings/</c>). Portable by design: no OS
/// user-profile folders (AppData, ~/.config) are used, and no
/// Windows/Linux branching is needed since <see cref="AppBaseDirectory"/>
/// resolves consistently on both. Copy the app folder anywhere and its
/// data comes with it.
/// </summary>
public sealed class PathProvider : IPathProvider
{
    private static IPathProvider? _current;
    /// <summary>
    /// The active path provider. Defaults to a singleton PathProvider instance.
    /// Tests can override via <see cref="Override(IPathProvider)"/>.
    /// </summary>
    public static IPathProvider Current
    {
        get => _current ??= new PathProvider();
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Replace the active provider. Useful for unit tests.</summary>
    public static void Override(IPathProvider provider) => _current = provider;

    /// <summary>Reset to the default singleton. Useful for test teardown.</summary>
    public static void Reset() => _current = null;

    private PathProvider() { }

    /// <summary>
    /// The application's base directory (where Jukebox.exe lives).
    /// </summary>
    public string AppBaseDirectory => AppDomain.CurrentDomain.BaseDirectory;

    /// <summary>
    /// Directory containing the host playback runtime libraries. The
    /// self-contained plugin packages live under plugins/.
    /// </summary>
    public string NativeLibDirectory =>
        Path.Combine(AppBaseDirectory, "lib");



    public string SettingsDirectory =>
        Path.Combine(AppBaseDirectory, "Settings");

    public string EqSettingsFile =>
        Path.Combine(SettingsDirectory, Constants.EqSettingsFileName);

    public string PlaylistsDirectory =>
        Path.Combine(AppBaseDirectory, "Playlists");

    public string CacheDirectory =>
        Path.Combine(AppBaseDirectory, "Cache");
}
