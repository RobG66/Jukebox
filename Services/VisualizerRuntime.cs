namespace Jukebox.Services;

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Avalonia.Controls;

public sealed class VisualizerRuntime : IVisualizerRuntime
{
    #region Private Fields
    private static readonly object _initGate = new();
    private static IVisualizerRuntime? _current;
    private static IVisualizerRuntime? _override;

    private readonly IPathProvider _pathProvider;
    private Assembly? _assembly;
    private Type? _projectMControlType;
    private MethodInfo? _startEngineMethod;
    private MethodInfo? _loadPresetMethod;
    private MethodInfo? _feedPcmMethod;
    private bool _probed;
    private string? _resolvedDllPath;
    private bool _feedPcmErrorLogged;
    #endregion

    #region Public Properties
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

    public bool IsAvailable => Directory.Exists(_pathProvider.ProjectMPresetsDirectory);
    #endregion

    #region Constructor
    public VisualizerRuntime(IPathProvider pathProvider)
    {
        _pathProvider = pathProvider ?? throw new ArgumentNullException(nameof(pathProvider));
    }
    #endregion

    #region Public Methods
    public static void Override(IVisualizerRuntime? runtime)
    {
        lock (_initGate)
        {
            _override = runtime;
        }
    }

    public static void Reset()
    {
        lock (_initGate)
        {
            _current = null;
            _override = null;
        }
    }

    public Control? CreateControl()
    {
        EnsureProbed();
        if (_projectMControlType == null) return null;
        try
        {
            return Activator.CreateInstance(_projectMControlType) as Control;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VisualizerRuntime] CreateControl failed: {ex.Message}");
            return null;
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
            var currentPresetDir = _pathProvider.CurrentPresetDirectory;
            Directory.CreateDirectory(currentPresetDir);
            
            foreach (var file in Directory.GetFiles(currentPresetDir))
            {
                try { File.Delete(file); } catch { }
            }

            string destPath = Path.Combine(currentPresetDir, Path.GetFileName(presetPath));
            File.Copy(presetPath, destPath, true);

            string sourceDir = Path.GetDirectoryName(presetPath) ?? "";
            string content = File.ReadAllText(presetPath);
            var regex = new System.Text.RegularExpressions.Regex(@"[a-zA-Z0-9_-]+\.(?:jpg|png|bmp|tga)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            
            foreach (System.Text.RegularExpressions.Match match in regex.Matches(content))
            {
                string textureName = match.Value;
                string sourceTex = Path.Combine(sourceDir, textureName);
                if (File.Exists(sourceTex))
                {
                    string destTex = Path.Combine(currentPresetDir, textureName);
                    File.Copy(sourceTex, destTex, true);
                }
            }

            _loadPresetMethod.Invoke(control, new object[] { destPath, true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VisualizerRuntime] LoadPreset failed: {ex.Message}");
            try
            {
                _loadPresetMethod.Invoke(control, new object[] { presetPath, true });
            }
            catch (Exception exFallback)
            {
                Debug.WriteLine($"[VisualizerRuntime] LoadPreset fallback failed: {exFallback.Message}");
            }
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
            if (!_feedPcmErrorLogged)
            {
                _feedPcmErrorLogged = true;
                Debug.WriteLine($"[VisualizerRuntime] FeedPcm failed: {ex.Message}");
            }
        }
    }

    public void TryDispose(Control? control)
    {
        if (control is IDisposable disposable)
        {
            try { disposable.Dispose(); }
            catch (Exception ex)
            {
                Trace.WriteLine($"[VisualizerRuntime] Dispose failed: {ex.Message}");
            }
        }
    }
    #endregion

    #region Private Methods
    private void EnsureProbed()
    {
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
            if (!Directory.Exists(_pathProvider.ProjectMPresetsDirectory))
            {
                Debug.WriteLine($"[VisualizerRuntime] Presets directory not found: {_pathProvider.ProjectMPresetsDirectory}");
                return;
            }

            string dllPath = _pathProvider.JukeboxVisualizationsDllPath;
            if (!File.Exists(dllPath))
            {
                Debug.WriteLine($"[VisualizerRuntime] Visualizations DLL not found at: {dllPath}");
                return;
            }

            _assembly = Assembly.LoadFrom(dllPath);
            _resolvedDllPath = dllPath;

            _projectMControlType = _assembly.GetType("JukeboxVisualizations.Controls.ProjectMControl");
            if (_projectMControlType == null)
            {
                Debug.WriteLine("[VisualizerRuntime] ProjectMControl type not found in assembly.");
                return;
            }

            _startEngineMethod = _projectMControlType.GetMethod("StartEngine", BindingFlags.Public | BindingFlags.Instance);
            _loadPresetMethod = _projectMControlType.GetMethod("LoadPreset", BindingFlags.Public | BindingFlags.Instance);
            _feedPcmMethod = _projectMControlType.GetMethod("FeedPcm", BindingFlags.Public | BindingFlags.Instance);

            if (_startEngineMethod == null || _loadPresetMethod == null || _feedPcmMethod == null)
            {
                Debug.WriteLine("[VisualizerRuntime] One or more ProjectMControl methods not found.");
                _projectMControlType = null;
                return;
            }

            Debug.WriteLine($"[VisualizerRuntime] Visualizer successfully enabled using: {_resolvedDllPath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VisualizerRuntime] Probe failed: {ex.Message}");
            _assembly = null;
            _projectMControlType = null;
        }
    }
    #endregion
}
