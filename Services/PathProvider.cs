using System;
using System.IO;

namespace Jukebox.Services;

/// <summary>
/// Default <see cref="IPathProvider"/> implementation. Uses the application's
/// base directory for bundled assets (ProjectM presets) and the OS-specific
/// ApplicationData folder for user settings.
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

    public string ProjectMRoot =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectM");

    public string ProjectMPresetsDirectory =>
        Path.Combine(ProjectMRoot, "Presets");

    public string ProjectMFavoritesDirectory =>
        Path.Combine(ProjectMPresetsDirectory, "Favorites");

    public string LastPresetFile =>
        Path.Combine(ProjectMRoot, "last_preset.txt");

    public string SettingsDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Constants.SettingsDirectoryName);

    public string EqSettingsFile =>
        Path.Combine(SettingsDirectory, Constants.EqSettingsFileName);
}
