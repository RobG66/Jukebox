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
    /// Directory containing all native runtime libraries (bass, libmpv,
    /// libprojectM, glew, etc.) AND the optional JukeboxVisualizations.dll
    /// managed wrapper. Flat layout — Windows .dll and Linux .so files
    /// coexist by extension. The loader code picks the right filename per
    /// OS at runtime.
    /// </summary>
    string NativeLibDirectory { get; }

    /// <summary>
    /// Path to the JukeboxVisualizations.dll managed wrapper. Lives in
    /// <see cref="NativeLibDirectory"/> (<c>&lt;appdir&gt;/lib/</c>)
    /// alongside the native libprojectM binary — keeping all optional
    /// drop-in files in one place.
    /// </summary>
    string JukeboxVisualizationsDllPath { get; }

    /// <summary>Root directory of the ProjectM preset assets (presets, textures).</summary>
    string ProjectMRoot { get; }

    /// <summary>Directory containing ProjectM preset .milk files.</summary>
    string ProjectMPresetsDirectory { get; }

    /// <summary>The "favorites" subfolder inside the presets directory.</summary>
    string ProjectMFavoritesDirectory { get; }

    /// <summary>File that stores the last-selected visualizer preset path.</summary>
    string LastPresetFile { get; }

    /// <summary>Per-user settings directory (Environment.SpecialFolder.ApplicationData).</summary>
    string SettingsDirectory { get; }

    /// <summary>Full path to the EQ settings JSON file.</summary>
    string EqSettingsFile { get; }
}
