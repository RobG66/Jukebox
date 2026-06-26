namespace Jukebox.Services;

using Avalonia.Controls;

/// <summary>
/// Optional, runtime-discovered visualizer abstraction.
///
/// <para>
/// The Jukebox project no longer holds a compile-time reference to the
/// <c>JukeboxVisualizations</c> companion assembly. Instead, the assembly
/// is discovered at runtime via reflection (see
/// <see cref="VisualizerRuntime"/>). If the assembly (and the
/// <c>ProjectM</c> drop-in folder) is present, visualizations activate;
/// otherwise the visualizer button is hidden and audio plays without any
/// ProjectM dependency.
/// </para>
///
/// <para>
/// The control returned by <see cref="CreateControl"/> is an
/// <see cref="Avalonia.Controls.Control"/> (the most-derived type
/// statically visible to the Jukebox). All ProjectM-specific operations
/// are exposed as instance methods on this interface so the rest of the
/// Jukebox can drive the visualizer without any compile-time knowledge
/// of <c>ProjectMControl</c>.
/// </para>
/// </summary>
public interface IVisualizerRuntime
{
    /// <summary>
    /// <c>true</c> if the <c>JukeboxVisualizations.dll</c> managed wrapper
    /// was successfully loaded AND the <c>ProjectM</c> drop-in folder
    /// (with <c>Presets</c>) is present. Drives the visibility of the
    /// visualizer button in the transport bar.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Create a new <c>ProjectMControl</c> instance via reflection. Returns
    /// <c>null</c> if <see cref="IsAvailable"/> is false. The returned
    /// control can be added directly to a <see cref="ContentControl"/>'s
    /// <c>Content</c> property.
    /// </summary>
    Control? CreateControl();

    /// <summary>
    /// Bind the control's <c>PresetPath</c> styled property to the given
    /// binding path on the control's <c>DataContext</c>. Equivalent to:
    /// <code>
    /// control[!ProjectMControl.PresetPathProperty] = new Binding(path);
    /// </code>
    /// </summary>
    void SetPresetPathBinding(Control control, string bindingPath);

    /// <summary>
    /// Start the ProjectM rendering engine on the control. No-op if the
    /// runtime is unavailable.
    /// </summary>
    void StartEngine(Control control);

    /// <summary>
    /// Load a preset (.milk file) into the control. No-op if the runtime
    /// is unavailable.
    /// </summary>
    void LoadPreset(Control control, string presetPath);

    /// <summary>
    /// Feed PCM audio samples (mono interleaved shorts) to the control
    /// for visualization. No-op if the runtime is unavailable.
    /// </summary>
    void FeedPcm(Control control, short[] pcm);

    /// <summary>
    /// Dispose the control if it implements <c>IDisposable</c>. Safe to
    /// call with <c>null</c> or on a non-disposable control. After this
    /// call, the control should be removed from the visual tree.
    /// </summary>
    void TryDispose(Control? control);
}
