namespace Jukebox.Services;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Jukebox.Extensions;

public sealed class VisualizerRuntime : IVisualizerRuntime
{
    #region Private Fields
    private static readonly object _initGate = new();
    private static IVisualizerRuntime? _current;
    private static IVisualizerRuntime? _override;

    // Regex cached as static readonly with Compiled flag.
    // Since LoadPreset is async and may fire rapidly (user clicking through
    // the picker tree), caching the compiled regex is important.
    private static readonly Regex TextureFileRegex = new(
        @"[a-zA-Z0-9_-]+\.(?:jpg|png|bmp|tga)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Serializes preset loads so that rapid clicks don't race. The
    // SemaphoreSlim ensures only one preset load runs at a time — the
    // second load waits for the first to finish before starting.
    private static readonly SemaphoreSlim _presetLoadSemaphore = new(1, 1);

    // Cancels any in-flight preset load when a new one is requested.
    // If the user clicks preset A then preset B quickly, A's background
    // Task checks this token before invoking LoadPresetData on the UI
    // thread — if canceled, it skips the invoke so B's load wins.
    private static CancellationTokenSource? _presetLoadCts;

    private readonly IPathProvider _pathProvider;
    private Assembly? _assembly;
    private Type? _projectMControlType;

    // Cached typed delegates instead of MethodInfo.
    //
    // Using MethodInfo.Invoke on every FeedPcm call (~33 times/sec) would allocate 
    // a new object[] for arguments and box parameters on the real-time audio thread, 
    // increasing GC pressure and causing occasional latency spikes.
    //
    // Delegate.CreateDelegate creates a typed delegate that can be invoked
    // directly — no boxing, no array allocation, no reflection overhead.
    // The delegates are created once in Probe() and reused for the lifetime
    // of the runtime.
    private Action<Control>? _startEngineAction;
    private Action<Control, string, bool>? _loadPresetAction;
    private Action<Control, string, bool>? _loadPresetDataAction;
    private Action<Control, short[]>? _feedPcmAction;

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
        if (_startEngineAction == null) return;
        try
        {
            _startEngineAction(control);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VisualizerRuntime] StartEngine failed: {ex.Message}");
        }
    }

    // LoadPreset runs file IO on a background thread.
    //
    // File IO (ReadAllText, WriteAllText, File.Copy for textures) is performed
    // on a background thread using Task.Run to prevent freezing the UI thread.
    //
    // The file IO is serialized via a SemaphoreSlim to prevent rapid-click races.
    // A CancellationTokenSource cancels in-flight loads when a new preset is 
    // selected — if the user clicks A then B quickly, A's load is canceled 
    // before it reaches the LoadPresetData invoke.
    //
    // Uses LoadPresetData(string content, bool smooth) via reflection when available, 
    // eliminating the duplicate file read that occurred when VisualizerRuntime wrote 
    // a normalized copy to current_preset/ and ProjectMControl.LoadPreset re-read it.
    //
    // Uses the static readonly TextureFileRegex instead of constructing a new Regex per call.
    public void LoadPreset(Control control, string presetPath)
    {
        EnsureProbed();

        // Cancel any in-flight load — the user clicked a new preset.
        _presetLoadCts?.Cancel();
        _presetLoadCts?.Dispose();
        _presetLoadCts = new CancellationTokenSource();
        var ct = _presetLoadCts.Token;

        Task.Run(async () =>
        {
            await _presetLoadSemaphore.WaitAsync(ct);
            try
            {
                if (ct.IsCancellationRequested) return;

                var currentPresetDir = _pathProvider.CurrentPresetDirectory;
                Directory.CreateDirectory(currentPresetDir);

                // Check if we are loading a preset that is already in current_preset/
                bool isFromCurrentPresetDir = false;
                try
                {
                    var fullPresetPath = Path.GetFullPath(presetPath);
                    var fullCurrentPresetDir = Path.GetFullPath(currentPresetDir);
                    isFromCurrentPresetDir = fullPresetPath.StartsWith(fullCurrentPresetDir, StringComparison.OrdinalIgnoreCase);
                }
                catch { }

                if (!isFromCurrentPresetDir)
                {
                    // Clean current_preset/ directory — remove stale files from
                    // previous preset loads (defensive: handles the case where
                    // a previous load crashed mid-write and left extra files).
                    foreach (var file in Directory.GetFiles(currentPresetDir))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }

                // Read the preset file and normalize line endings to Unix
                // format (\n) to prevent parsing failures in libprojectM
                // on Linux (defensive — projectM's parser handles \r\n
                // correctly, but normalization costs nothing).
                string content = File.ReadAllText(presetPath);
                string normalizedContent = content.Replace("\r\n", "\n");

                // Write normalized content to current_preset/ for restore
                // on next launch. This is the "restore point" —
                // JukeboxVisualizerViewModel.InitializeAsync scans this
                // directory for a .milk file at startup.
                string destPath = Path.Combine(currentPresetDir, Path.GetFileName(presetPath));
                File.WriteAllText(destPath, normalizedContent);

                // Copy referenced textures (jpg/png/bmp/tga) from the
                // preset's source directory to current_preset/ so they're
                // available alongside the preset.
                string sourceDir = Path.GetDirectoryName(presetPath) ?? "";
                foreach (Match match in TextureFileRegex.Matches(content))
                {
                    if (ct.IsCancellationRequested) return;
                    string textureName = match.Value;
                    string sourceTex = Path.Combine(sourceDir, textureName);
                    if (File.Exists(sourceTex))
                    {
                        string destTex = Path.Combine(currentPresetDir, textureName);
                        File.Copy(sourceTex, destTex, true);
                    }
                }

                if (ct.IsCancellationRequested) return;

                // Invoke on the UI thread. The reflection call itself is
                // cheap (just enqueues to a ConcurrentQueue inside
                // ProjectMControl), but we post to the UI thread for
                // consistency with Avalonia's threading model.
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        // Prefer LoadPresetData (avoids re-reading the file
                        // inside ProjectMControl). Fall back to LoadPreset(path)
                        // if the delegate isn't available (older wrapper version
                        // that doesn't have LoadPresetData).
                        if (_loadPresetDataAction != null)
                        {
                            _loadPresetDataAction(control, normalizedContent, true);
                        }
                        else if (_loadPresetAction != null)
                        {
                            _loadPresetAction(control, destPath, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[VisualizerRuntime] LoadPreset invoke failed: {ex.Message}");
                        // Fallback: try LoadPreset(path) if LoadPresetData failed
                        try
                        {
                            _loadPresetAction?.Invoke(control, destPath, true);
                        }
                        catch (Exception exFallback)
                        {
                            Debug.WriteLine($"[VisualizerRuntime] LoadPreset fallback failed: {exFallback.Message}");
                        }
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Expected — user clicked a new preset while this one was loading.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VisualizerRuntime] LoadPreset background work failed: {ex.Message}");
                // Fallback: try loading directly from the original path on
                // the UI thread. This bypasses the current_preset/ copy and
                // texture handling, but at least attempts to load something.
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        _loadPresetAction?.Invoke(control, presetPath, true);
                    }
                    catch (Exception exFallback)
                    {
                        Debug.WriteLine($"[VisualizerRuntime] LoadPreset fallback failed: {exFallback.Message}");
                    }
                });
            }
            finally
            {
                _presetLoadSemaphore.Release();
            }
        }).SafeFireAndForget(nameof(LoadPreset));
    }

    public void FeedPcm(Control control, short[] pcm)
    {
        if (_feedPcmAction == null) return;
        try
        {
            _feedPcmAction(control, pcm);
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

            // Create typed delegates once instead of using MethodInfo.Invoke per call. 
            // Delegate.CreateDelegate produces a directly-callable delegate — no boxing, 
            // no object[] allocation, no per-call reflection overhead.
            //
            // Method signatures in ProjectMControl:
            //   void StartEngine()
            //   void LoadPreset(string path, bool smooth)
            //   void LoadPresetData(string content, bool smooth)
            //   void FeedPcm(short[] samples)
            var startEngineMethod = _projectMControlType.GetMethod("StartEngine", BindingFlags.Public | BindingFlags.Instance);
            var loadPresetMethod = _projectMControlType.GetMethod("LoadPreset", BindingFlags.Public | BindingFlags.Instance);
            var loadPresetDataMethod = _projectMControlType.GetMethod("LoadPresetData", BindingFlags.Public | BindingFlags.Instance);
            var feedPcmMethod = _projectMControlType.GetMethod("FeedPcm", BindingFlags.Public | BindingFlags.Instance);

            if (startEngineMethod == null || loadPresetMethod == null || feedPcmMethod == null)
            {
                Debug.WriteLine("[VisualizerRuntime] One or more ProjectMControl methods not found.");
                _projectMControlType = null;
                return;
            }

            // Create compiled expression delegates once instead of using Delegate.CreateDelegate.
            // Delegate.CreateDelegate fails because Control is a base type of ProjectMControl,
            // which violates delegate parameter covariance/type safety. Using Expressions compiled
            // at runtime allows us to cast the control parameter and compile direct-call delegates.
            try
            {
                var controlParam = Expression.Parameter(typeof(Control), "control");
                var castControl = Expression.Convert(controlParam, _projectMControlType);

                // StartEngine
                var startCall = Expression.Call(castControl, startEngineMethod);
                _startEngineAction = Expression.Lambda<Action<Control>>(startCall, controlParam).Compile();

                // LoadPreset
                var pathParam = Expression.Parameter(typeof(string), "path");
                var smoothParam = Expression.Parameter(typeof(bool), "smooth");
                var loadCall = Expression.Call(castControl, loadPresetMethod, pathParam, smoothParam);
                _loadPresetAction = Expression.Lambda<Action<Control, string, bool>>(loadCall, controlParam, pathParam, smoothParam).Compile();

                // FeedPcm
                var pcmParam = Expression.Parameter(typeof(short[]), "pcm");
                var feedCall = Expression.Call(castControl, feedPcmMethod, pcmParam);
                _feedPcmAction = Expression.Lambda<Action<Control, short[]>>(feedCall, controlParam, pcmParam).Compile();

                // LoadPresetData (optional)
                if (loadPresetDataMethod != null)
                {
                    var contentParam = Expression.Parameter(typeof(string), "content");
                    var smoothDataParam = Expression.Parameter(typeof(bool), "smooth");
                    var loadDataCall = Expression.Call(castControl, loadPresetDataMethod, contentParam, smoothDataParam);
                    _loadPresetDataAction = Expression.Lambda<Action<Control, string, bool>>(loadDataCall, controlParam, contentParam, smoothDataParam).Compile();
                    Debug.WriteLine($"[VisualizerRuntime] Visualizer successfully enabled using: {_resolvedDllPath} (LoadPresetData available — no duplicate file IO)");
                }
                else
                {
                    Debug.WriteLine($"[VisualizerRuntime] Visualizer successfully enabled using: {_resolvedDllPath} (LoadPresetData not found — using LoadPreset path fallback)");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VisualizerRuntime] Failed to compile delegates via Expressions: {ex.Message}");
                _projectMControlType = null;
                return;
            }
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
