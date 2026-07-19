using Jukebox.Plugin.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;

namespace Jukebox.Services;

/// <summary>
/// Scans the <c>plugins/</c> folder at startup, loads each plugin
/// assembly, finds the <see cref="IJukeboxMediaBrowser"/> implementation
/// in each, instantiates it, and calls <see cref="IJukeboxMediaBrowser.InitializeAsync"/>.
///
/// <para><b>Discovery convention:</b> the loader inspects assemblies under
/// <c>&lt;appdir&gt;/plugins/*/</c> — each installed extension lives in its own
/// subfolder so its dependencies don't clash with other extensions. An
/// assembly is a media-browser plugin only when it contains a concrete
/// <see cref="IJukeboxMediaBrowser"/> implementation.</para>
///
/// <para><b>Isolation:</b> every plugin is wrapped in try/catch at every
/// stage (load, construct, initialize). A broken plugin is silently
/// skipped with a <see cref="Debug.WriteLine"/> log — it never takes
/// down the app. The user sees the problem as "the tab didn't appear"
/// and can remove the broken DLL to clean up.</para>
///
/// <para><b>Assembly resolution:</b> each plugin folder gets its own
/// <c>AssemblyLoadContext</c>. Assemblies already loaded by the host are
/// shared with the plugin, while private plugin dependencies are resolved
/// from that plugin's folder. This prevents duplicate copies of the plugin
/// contract and Avalonia assemblies from being loaded.</para>
/// </summary>
public static class PluginLoader
{
    /// <summary>
    /// Load every implementation of a supported plugin contract from
    /// <c>&lt;appdir&gt;/plugins/</c>.
    /// </summary>
    public static async Task<LoadedPlugins> LoadAllAsync(IJukeboxMediaBrowserContextFactory contextFactory)
    {
        var pluginsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins");
        var browsers = new List<IJukeboxMediaBrowser>();
        var visualizers = new List<IJukeboxVisualizerPlugin>();
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

        if (!Directory.Exists(pluginsDir))
        {
            Debug.WriteLine("[PluginLoader] plugins/ folder does not exist — no plugins loaded.");
            return new LoadedPlugins(browsers, visualizers);
        }

        var pluginFolders = Directory.GetDirectories(pluginsDir);
        Debug.WriteLine($"[PluginLoader] Scanning {pluginFolders.Length} plugin folder(s)...");

        // Folder and assembly names are deliberately irrelevant. The only
        // discovery rule is implementation of the shared plugin contract.
        foreach (var pluginFolder in pluginFolders)
        {
            var folderStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var folderName = Path.GetFileName(pluginFolder);
            Debug.WriteLine($"[PluginLoader] Scanning folder: {folderName}");

            var managedAssemblyPaths = Directory.GetFiles(pluginFolder, "*.dll")
                .Where(IsManagedAssembly)
                .ToArray();

            // Some visualizer folders contain only native libraries. They
            // are consumed by a managed plugin in another folder and are not
            // plugin assemblies themselves.
            if (managedAssemblyPaths.Length == 0)
            {
                Debug.WriteLine($"[PluginLoader] Skipped {folderName}: no managed assemblies.");
                continue;
            }

            var loadContext = new PluginLoadContext(pluginFolder, managedAssemblyPaths);

            foreach (var assemblyPath in managedAssemblyPaths)
            {
                if (loadContext.IsSharedWithHost(assemblyPath))
                {
                    continue;
                }

                if (loadContext.IsAlreadyLoaded(assemblyPath))
                {
                    continue;
                }

                var loaded = await TryLoadPluginsAsync(
                    assemblyPath,
                    pluginFolder,
                    loadContext,
                    contextFactory);
                browsers.AddRange(loaded.MediaBrowsers);
                visualizers.AddRange(loaded.Visualizers);
            }

            Debug.WriteLine(
                $"[PluginLoader] Finished {folderName} in {folderStopwatch.ElapsedMilliseconds}ms.");
        }

        Debug.WriteLine(
            $"[PluginLoader] Scan completed in {totalStopwatch.ElapsedMilliseconds}ms: " +
            $"{browsers.Count} browser(s), {visualizers.Count} visualizer(s).");

        return new LoadedPlugins(
            browsers
                .OrderBy(browser => browser.SortOrder)
                .ThenBy(browser => browser.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            visualizers
                .OrderBy(visualizer => visualizer.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    private static async Task<LoadedPlugins> TryLoadPluginsAsync(
        string dllPath,
        string pluginFolder,
        PluginLoadContext loadContext,
        IJukeboxMediaBrowserContextFactory contextFactory)
    {
        var folderName = Path.GetFileName(pluginFolder);
        var browsers = new List<IJukeboxMediaBrowser>();
        var visualizers = new List<IJukeboxVisualizerPlugin>();

        try
        {
            var assemblyStopwatch = System.Diagnostics.Stopwatch.StartNew();
            Debug.WriteLine($"[PluginLoader] Inspecting {Path.GetFileName(dllPath)}...");
            var assembly = loadContext.LoadFromAssemblyPath(dllPath);
            var pluginTypes = FindPluginTypes(assembly).ToList();
            if (pluginTypes.Count == 0)
            {
                Debug.WriteLine(
                    $"[PluginLoader] No plugin types in {Path.GetFileName(dllPath)} " +
                    $"({assemblyStopwatch.ElapsedMilliseconds}ms).");
                return new LoadedPlugins(browsers, visualizers);
            }

            foreach (var pluginType in pluginTypes)
            {
                try
                {
                    var plugin = Activator.CreateInstance(pluginType);
                    if (plugin is IJukeboxMediaBrowser browser)
                    {
                        Debug.WriteLine($"[PluginLoader] Initializing browser: {browser.Id}");
                        var context = contextFactory.CreateContext(browser.Id);
                        await browser.InitializeAsync(context);
                        browsers.Add(browser);
                        Debug.WriteLine(
                            $"[PluginLoader] Loaded browser: {browser.DisplayName} ({browser.Id}) " +
                            $"in {assemblyStopwatch.ElapsedMilliseconds}ms.");
                    }
                    else if (plugin is IJukeboxVisualizerPlugin visualizer)
                    {
                        Debug.WriteLine($"[PluginLoader] Initializing visualizer: {visualizer.Id}");
                        await visualizer.InitializeAsync();
                        if (visualizer.IsAvailable)
                        {
                            visualizers.Add(visualizer);
                            Debug.WriteLine(
                                $"[PluginLoader] Loaded visualizer: {visualizer.DisplayName} ({visualizer.Id}) " +
                                $"in {assemblyStopwatch.ElapsedMilliseconds}ms.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PluginLoader] Failed to initialize {pluginType.Name} in {folderName}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PluginLoader] Failed to load {folderName}: {ex.Message}");
        }

        return new LoadedPlugins(browsers, visualizers);
    }

    private static IEnumerable<Type> FindPluginTypes(Assembly assembly)
    {
        // Look for concrete classes implementing IJukeboxMediaBrowser.
        // We use GetTypes() in a try/catch because some types may fail
        // to load if their dependencies are missing — skip those.
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray()!;
        }

        return types.Where(type =>
            !type.IsAbstract &&
            !type.IsInterface &&
            (typeof(IJukeboxMediaBrowser).IsAssignableFrom(type) ||
             typeof(IJukeboxVisualizerPlugin).IsAssignableFrom(type)));
    }

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private static class Debug
    {
        public static void WriteLine(string message)
        {
            System.Diagnostics.Debug.WriteLine(message);
            Console.WriteLine(message);
        }
    }

    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly IReadOnlyList<AssemblyDependencyResolver> _resolvers;

        public PluginLoadContext(string pluginFolder, IReadOnlyList<string> managedAssemblyPaths)
            : base($"Jukebox.Plugin:{Path.GetFileName(pluginFolder)}")
        {
            if (managedAssemblyPaths.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No managed plugin assembly found in '{pluginFolder}'.");
            }

            // A folder can contain more than one managed component and its
            // dependencies. Use every component as a resolver root instead of
            // relying on unspecified Directory.GetFiles ordering to pick the
            // plugin entry assembly.
            _resolvers = managedAssemblyPaths
                .Select(path => new AssemblyDependencyResolver(path))
                .ToArray();
        }

        public bool IsSharedWithHost(string assemblyPath)
        {
            var assemblyName = AssemblyName.GetAssemblyName(assemblyPath).Name;
            return assemblyName != null && FindHostAssembly(assemblyName) != null;
        }

        public bool IsAlreadyLoaded(string assemblyPath)
        {
            var assemblyName = AssemblyName.GetAssemblyName(assemblyPath).Name;
            return assemblyName != null && Assemblies.Any(assembly =>
                string.Equals(
                    assembly.GetName().Name,
                    assemblyName,
                    StringComparison.OrdinalIgnoreCase));
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name != null)
            {
                var hostAssembly = FindHostAssembly(assemblyName.Name);
                if (hostAssembly != null)
                {
                    return hostAssembly;
                }
            }

            // Resolve plugin-private dependencies locally before falling back
            // to the default context. Calling Default.LoadFromAssemblyName
            // first makes every private dependency pay for a failed host probe
            // and was the source of intermittent multi-second startup stalls.
            foreach (var resolver in _resolvers)
            {
                var assemblyPath = resolver.ResolveAssemblyToPath(assemblyName);
                if (assemblyPath != null)
                {
                    return LoadFromAssemblyPath(assemblyPath);
                }
            }

            // Returning null delegates normal host dependency resolution to
            // the runtime's default load context without a redundant probe.
            return null;
        }

        private static Assembly? FindHostAssembly(string simpleName)
        {
            return AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly =>
                string.Equals(
                    assembly.GetName().Name,
                    simpleName,
                    StringComparison.OrdinalIgnoreCase));
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            foreach (var resolver in _resolvers)
            {
                var libraryPath = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
                if (libraryPath != null)
                {
                    return LoadUnmanagedDllFromPath(libraryPath);
                }
            }

            return IntPtr.Zero;
        }
    }
}

public sealed class LoadedPlugins
{
    public LoadedPlugins(
        IReadOnlyList<IJukeboxMediaBrowser> mediaBrowsers,
        IReadOnlyList<IJukeboxVisualizerPlugin> visualizers)
    {
        MediaBrowsers = mediaBrowsers;
        Visualizers = visualizers;
    }

    public IReadOnlyList<IJukeboxMediaBrowser> MediaBrowsers { get; }
    public IReadOnlyList<IJukeboxVisualizerPlugin> Visualizers { get; }
}

/// <summary>
/// Factory that creates a per-plugin context. Implemented by the main
/// app so the loader doesn't need to know about the host's playlist VM
/// or path provider.
/// </summary>
public interface IJukeboxMediaBrowserContextFactory
{
    IJukeboxMediaBrowserContext CreateContext(string pluginId);
}
