using System;
using System.IO;

namespace Jukebox.Services;

/// <summary>
/// Default <see cref="IPathProvider"/> implementation. Uses the application's
/// base directory for native libraries (<c>lib/</c>), ProjectM preset assets
/// (<c>ProjectM/</c>), and the OS-specific ApplicationData folder for user
/// settings.
///
/// Cross-platform behavior:
///  - Windows: SettingsDirectory = C:\Users\&lt;user&gt;\AppData\Roaming\Jukebox
///  - Linux:   SettingsDirectory = ~/.config/Jukebox
///  - macOS:   SettingsDirectory = ~/.config/Jukebox
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
    /// Directory containing all native runtime libraries (bass, libmpv,
    /// libprojectM, glew) AND the optional JukeboxVisualizations.dll
    /// managed wrapper. Flat layout — Windows .dll and Linux .so files
    /// coexist by extension; the loader picks the right filename per OS.
    /// </summary>
    public string NativeLibDirectory =>
        Path.Combine(AppBaseDirectory, "lib");

    /// <summary>
    /// Path to the JukeboxVisualizations.dll managed wrapper. Lives in
    /// <see cref="NativeLibDirectory"/> (<c>&lt;appdir&gt;/lib/</c>)
    /// alongside the native libprojectM binary — keeping all optional
    /// drop-in files in one place.
    /// </summary>
    public string JukeboxVisualizationsDllPath =>
        Path.Combine(NativeLibDirectory, "JukeboxVisualizations.dll");

    /// <summary>
    /// Root directory of the ProjectM preset assets (presets, textures).
    /// Contains ONLY preset data — the native libprojectM binary lives in
    /// <see cref="NativeLibDirectory"/>.
    /// </summary>
    public string ProjectMRoot =>
        Path.Combine(AppBaseDirectory, "ProjectM");

    public string ProjectMPresetsDirectory =>
        Path.Combine(ProjectMRoot, "presets");

    public string ProjectMFavoritesDirectory =>
        Path.Combine(ProjectMPresetsDirectory, "Favorites");

    public string CurrentPresetDirectory =>
        Path.Combine(ProjectMRoot, "current_preset");

    public string SettingsDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Constants.SettingsDirectoryName);

    public string EqSettingsFile =>
        Path.Combine(SettingsDirectory, Constants.EqSettingsFileName);
}
