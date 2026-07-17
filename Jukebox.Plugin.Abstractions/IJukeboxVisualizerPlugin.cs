using Avalonia.Controls;
using System.Threading.Tasks;

namespace Jukebox.Plugin.Abstractions;

/// <summary>
/// Defines the contract for all visualizer plugins in the Jukebox system.
/// </summary>
public interface IJukeboxVisualizerPlugin
{
    /// <summary>
    /// Unique stable identifier, e.g. "projectm". Lowercase, no spaces.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Display name shown in the engine selector ComboBox, e.g. "ProjectM".
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Called once at startup. Load settings, verify native deps, etc.
    /// Must not throw — catch and log internally.
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// True if the native engine is present (regardless of preset files on disk).
    /// Drives IsVisualizerAvailable in the host VM.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Creates the Avalonia rendering control (e.g. OpenGlControlBase subclass).
    /// </summary>
    Control? CreateControl();

    /// <summary>
    /// Starts the rendering engine on the created control.
    /// </summary>
    void StartEngine(Control control);

    /// <summary>
    /// Feeds PCM audio samples for visualization.
    /// </summary>
    void FeedPcm(Control control, short[] pcm);

    /// <summary>
    /// Safe disposal — called when the control is removed from the visual tree.
    /// </summary>
    void TryDispose(Control? control);

    /// <summary>
    /// True if the plugin supports selectable preset files.
    /// When true, the host renders the randomizer + preset tree UI.
    /// </summary>
    bool SupportsPresets { get; }

    /// <summary>
    /// Preset asset paths — only relevant when SupportsPresets is true.
    /// </summary>
    string? PresetsDirectory { get; }
    string? FavoritesDirectory { get; }
    string? CurrentPresetDirectory { get; }

    /// <summary>
    /// Load a specific preset into the control.
    /// No-op if SupportsPresets is false or control is null.
    /// </summary>
    void LoadPreset(Control control, string presetPath);

    /// <summary>
    /// Optional: plugin supplies its own settings panel.
    /// When non-null, the host embeds this UserControl in the
    /// Dynamic Controls Section instead of the preset tree.
    /// </summary>
    UserControl? CreateSettingsView() => null;
}
