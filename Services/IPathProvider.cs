namespace Jukebox.Services;

/// <summary>
/// Provides canonical filesystem paths for the Jukebox application.
/// Single point of change for all path-related decisions — addresses
/// Smell Test Report §6.3 (5 occurrences of duplicated
/// Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ...) calls)
/// and §7.2 item #7.
///
/// The default implementation is <see cref="PathProvider"/> (singleton via
/// <see cref="PathProvider.Instance"/>). To override paths in tests,
/// call <see cref="PathProvider.Override(IPathProvider)"/> with a stub.
/// </summary>
public interface IPathProvider
{
    /// <summary>Root directory of the bundled ProjectM assets (presets, textures).</summary>
    string ProjectMRoot { get; }

    /// <summary>Directory containing ProjectM preset .milk files.</summary>
    string ProjectMPresetsDirectory { get; }

    /// <summary>The "Favorites" subfolder inside the presets directory.</summary>
    string ProjectMFavoritesDirectory { get; }

    /// <summary>File that stores the last-selected visualizer preset path.</summary>
    string LastPresetFile { get; }

    /// <summary>Per-user settings directory (Environment.SpecialFolder.ApplicationData).</summary>
    string SettingsDirectory { get; }

    /// <summary>Full path to the EQ settings JSON file.</summary>
    string EqSettingsFile { get; }
}
