namespace Jukebox.Services;

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

/// <summary>
/// Reflection-based <see cref="IVisualizerRuntime"/> that loads the
/// <c>JukeboxVisualizations.dll</c> managed wrapper at runtime, without
/// any compile-time dependency on the assembly.
///
/// <para>
/// <b>Discovery sequence</b> — the runtime is considered available only
/// if ALL of the following are true:
/// </para>
/// <list type="number">
///   <item>The <c>ProjectM</c> preset folder exists in the application's
///       base directory (i.e. <c>ProjectMPresetsDirectory</c> is present
///       on disk).</item>
///   <item><c>JukeboxVisualizations.dll</c> exists in
///       <c>&lt;appdir&gt;/lib/</c> (alongside the native libprojectM
///       binary — all optional drop-in files live in the same flat
///       <c>lib/</c> folder). The path is exposed via
///       <see cref="IPathProvider.JukeboxVisualizationsDllPath"/>.</item>
///   <item>The assembly loads successfully and exposes the
///       <c>JukeboxVisualizations.Controls.ProjectMControl</c> type with
///       the expected <c>PresetPathProperty</c>,
///       <c>StartEngine</c>, <c>LoadPreset</c>, and <c>FeedPcm</c>
///       members.</item>
/// </list>
///
/// <para>
/// Type and member lookups are cached after the first successful probe so
/// repeated calls (per PCM buffer, per preset change) do not pay a
/// reflection cost.
/// </para>
/// </summary>
public sealed class VisualizerRuntime : IVisualizerRuntime
{
    private static readonly object _initGate = new();
    private static IVisualizerRuntime? _current;
    private static IVisualizerRuntime? _override;

    /// <summary>
    /// The active runtime. Defaults to a lazily-initialized
    /// <see cref="VisualizerRuntime"/> bound to
    /// <see cref="PathProvider.Current"/>. Tests can replace it via
    /// <see cref="Override(IVisualizerRuntime?)"/>.
    /// </summary>
    public static IVisualizerRuntime Current
    {
        get
        {
            lock (_initGate)
            {
                if (_override != null) return _override;
                _current ??= new VisualizerRuntime(PathProvider.Current);
                return _current;
            }
        }
    }

    /// <summary>
    /// Replace the active runtime. Pass <c>null</c> to revert to the
    /// default lazily-created <see cref="VisualizerRuntime"/>. Useful for
    /// unit tests, which typically inject a stub that always reports
    /// <see cref="IsAvailable"/> = false.
    /// </summary>
    public static void Override(IVisualizerRuntime? runtime)
    {
        lock (_initGate)
        {
            _override = runtime;
        }
    }

    /// <summary>
    /// Force a fresh probe on the next access. Useful if the ProjectM
    /// folder was dropped in after the runtime was first accessed.
    /// </summary>
    public static void Reset()
    {
        lock (_initGate)
        {
            _current = null;
            _override = null;
        }
    }

    private readonly IPathProvider _pathProvider;

    // Cached reflection state — populated on first access.
    private Assembly? _assembly;
    private Type? _projectMControlType;
    private AvaloniaProperty? _presetPathProperty;
    private MethodInfo? _startEngineMethod;
    private MethodInfo? _loadPresetMethod;
    private MethodInfo? _feedPcmMethod;
    private bool _probed;
    private string? _resolvedDllPath;
    private bool _feedPcmErrorLogged;

    public VisualizerRuntime(IPathProvider pathProvider)
    {
        _pathProvider = pathProvider ?? throw new ArgumentNullException(nameof(pathProvider));
    }

    public bool IsAvailable
    {
        get
        {
            // Show the visualizer button whenever the ProjectM drop-in
            // folder is present (with Presets). The actual
            // JukeboxVisualizations.dll probe happens lazily in
            // CreateControl — if the wrapper is missing or fails to load,
            // the button still appears but the visualizer won't render
            // (a clear error is logged). This avoids the button being
            // hidden when the user has dropped in a ProjectM folder that
            // doesn't quite match the exact layout we probe for.
            return Directory.Exists(_pathProvider.ProjectMPresetsDirectory);
        }
    }

    public Control? CreateControl()
    {
        EnsureProbed();
        if (_projectMControlType == null) return null;
        try
        {
            var instance = Activator.CreateInstance(_projectMControlType);
            return instance as Control;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VisualizerRuntime] CreateControl failed: {ex.Message}");
            return null;
        }
    }

    public void SetPresetPathBinding(Control control, string bindingPath)
    {
        EnsureProbed();
        if (_presetPathProperty == null || control is not AvaloniaObject ao) return;
        try
        {
            // The `control[!Property] = binding` trick only exists as compile-time
            // sugar on the strongly-typed indexer — it isn't reachable through
            // reflection. AvaloniaObject.Bind() is the actual runtime API for
            // setting up a binding imperatively; the plain object indexer used
            // previously calls SetValue() and tries to assign the Binding object
            // itself as the property value, which throws ArgumentException.
            ao.Bind(_presetPathProperty, new Binding(bindingPath));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VisualizerRuntime] SetPresetPathBinding failed: {ex.Message}");
        }
    }

    public void StartEngine(Control control)
    {
        EnsureProbed();
        if (_startEngineMethod == null) return;
        try
        {
            _startEngineMethod.Invoke(control, null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VisualizerRuntime] StartEngine failed: {ex.Message}");
        }
    }

    public void LoadPreset(Control control, string presetPath)
    {
        EnsureProbed();
        if (_loadPresetMethod == null) return;
        try
        {
            // MethodInfo.Invoke does not honor C# default parameter values —
            // that's purely a caller-side/compile-time feature. ProjectMControl.
            // LoadPreset(string path, bool smooth = true) takes two parameters,
            // so both must be supplied here or this throws
            // TargetParameterCountException, silently dropping every preset load.
            _loadPresetMethod.Invoke(control, new object[] { presetPath, true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VisualizerRuntime] LoadPreset failed: {ex.Message}");
        }
    }

    public void FeedPcm(Control control, short[] pcm)
    {
        EnsureProbed();
        if (_feedPcmMethod == null) return;
        try
        {
            _feedPcmMethod.Invoke(control, new object[] { pcm });
        }
        catch (Exception ex)
        {
            // PCM feeds happen ~20×/sec — only log once to avoid spam.
            if (!_feedPcmErrorLogged)
            {
                _feedPcmErrorLogged = true;
                Debug.WriteLine(
                    $"[VisualizerRuntime] FeedPcm failed (further errors suppressed): {ex.Message}");
            }
        }
    }

    public void TryDispose(Control? control)
    {
        if (control == null) return;
        if (control is IDisposable disposable)
        {
            try { disposable.Dispose(); }
            catch (Exception ex)
            {
                Trace.WriteLine($"[VisualizerRuntime] Dispose failed: {ex.Message}");
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────
    //  Reflection probe — runs at most once per VisualizerRuntime
    //  instance. All cached fields are populated atomically; if any step
    //  fails, IsAvailable returns false.
    // ─────────────────────────────────────────────────────────────────

    private void EnsureProbed()
    {
        // Cache only successful probes — if the previous probe failed
        // (e.g. JukeboxVisualizations.dll was missing), retry on the
        // next call so the user can drop in the DLL while the app is
        // running and have it picked up without a restart.
        if (_probed && _projectMControlType != null) return;
        lock (_initGate)
        {
            if (_probed && _projectMControlType != null) return;
            Probe();
            _probed = true;
        }
    }

    private void Probe()
    {
        try
        {
            // Step 1: ProjectM drop-in folder must exist with Presets.
            if (!Directory.Exists(_pathProvider.ProjectMPresetsDirectory))
            {
                Debug.WriteLine(
                    $"[VisualizerRuntime] ProjectM presets directory not found: " +
                    $"{_pathProvider.ProjectMPresetsDirectory}. Visualizer disabled.");
                return;
            }

            // Step 2: Locate JukeboxVisualizations.dll.
            //   Lives in <appdir>/lib/ alongside the native libprojectM
            //   binary — all optional drop-in files in one flat folder.
            string dllPath = _pathProvider.JukeboxVisualizationsDllPath;
            if (!File.Exists(dllPath))
            {
                Debug.WriteLine(
                    "[VisualizerRuntime] JukeboxVisualizations.dll not found at " +
                    $"{dllPath}. Visualizer disabled. The managed wrapper should " +
                    "be placed in the lib/ folder next to Jukebox.exe (alongside " +
                    "the native libprojectM binary).");
                return;
            }

            // Step 3: Load the assembly.
            _assembly = Assembly.LoadFrom(dllPath);
            _resolvedDllPath = dllPath;

            // Step 4: Locate ProjectMControl type.
            _projectMControlType = _assembly.GetType("JukeboxVisualizations.Controls.ProjectMControl");
            if (_projectMControlType == null)
            {
                Debug.WriteLine(
                    "[VisualizerRuntime] JukeboxVisualizations.Controls.ProjectMControl type " +
                    "not found in assembly. Visualizer disabled.");
                return;
            }

            // Step 5: Locate PresetPathProperty (Avalonia styled property field).
            var propField = _projectMControlType.GetField(
                "PresetPathProperty",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            _presetPathProperty = propField?.GetValue(null) as AvaloniaProperty;
            if (_presetPathProperty == null)
            {
                Debug.WriteLine(
                    "[VisualizerRuntime] ProjectMControl.PresetPathProperty field not found. " +
                    "Visualizer disabled.");
                return;
            }

            // Step 6: Locate instance methods.
            _startEngineMethod = _projectMControlType.GetMethod(
                "StartEngine", BindingFlags.Public | BindingFlags.Instance);
            _loadPresetMethod = _projectMControlType.GetMethod(
                "LoadPreset", BindingFlags.Public | BindingFlags.Instance);
            _feedPcmMethod = _projectMControlType.GetMethod(
                "FeedPcm", BindingFlags.Public | BindingFlags.Instance);

            if (_startEngineMethod == null || _loadPresetMethod == null || _feedPcmMethod == null)
            {
                Debug.WriteLine(
                    "[VisualizerRuntime] One or more ProjectMControl methods not found " +
                    $"(StartEngine={_startEngineMethod != null}, " +
                    $"LoadPreset={_loadPresetMethod != null}, " +
                    $"FeedPcm={_feedPcmMethod != null}). Visualizer disabled.");
                _projectMControlType = null; // force IsAvailable = false
                return;
            }

            Debug.WriteLine(
                $"[VisualizerRuntime] Visualizer enabled. Loaded '{_resolvedDllPath}'. " +
                $"ProjectMControl type: {_projectMControlType.FullName}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VisualizerRuntime] Probe failed: {ex.Message}");
            _assembly = null;
            _projectMControlType = null;
        }
    }
}
