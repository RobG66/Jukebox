using Jukebox.Plugin.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
/// <para><b>Assembly resolution:</b> when a plugin assembly has
/// dependencies in its own folder, .NET's default <c>AssemblyLoadContext</c>
/// won't find them. We hook <c>AppDomain.AssemblyResolve</c> to probe
/// the plugin's folder for satellite assemblies.</para>
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

        if (!Directory.Exists(pluginsDir))
        {
            Debug.WriteLine("[PluginLoader] plugins/ folder does not exist — no plugins loaded.");
            return new LoadedPlugins(browsers, visualizers);
        }

        // Folder and assembly names are deliberately irrelevant. The only
        // discovery rule is implementation of the shared plugin contract.
        foreach (var pluginFolder in Directory.GetDirectories(pluginsDir))
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
                ResolveFromPluginFolder(args, pluginFolder);

            foreach (var assemblyPath in Directory.GetFiles(pluginFolder, "*.dll"))
            {
                if (!IsManagedAssembly(assemblyPath))
                {
                    continue;
                }

                var loaded = await TryLoadPluginsAsync(assemblyPath, pluginFolder, contextFactory);
                browsers.AddRange(loaded.MediaBrowsers);
                visualizers.AddRange(loaded.Visualizers);
            }
        }

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
        string dllPath, string pluginFolder, IJukeboxMediaBrowserContextFactory contextFactory)
    {
        var folderName = Path.GetFileName(pluginFolder);
        var browsers = new List<IJukeboxMediaBrowser>();
        var visualizers = new List<IJukeboxVisualizerPlugin>();

        try
        {
            var assembly = Assembly.LoadFrom(dllPath);
            var pluginTypes = FindPluginTypes(assembly).ToList();
            if (pluginTypes.Count == 0)
            {
                return new LoadedPlugins(browsers, visualizers);
            }

            foreach (var pluginType in pluginTypes)
            {
                try
                {
                    var plugin = Activator.CreateInstance(pluginType);
                    if (plugin is IJukeboxMediaBrowser browser)
                    {
                        var context = contextFactory.CreateContext(browser.Id);
                        await browser.InitializeAsync(context);
                        browsers.Add(browser);
                        Debug.WriteLine($"[PluginLoader] Loaded browser: {browser.DisplayName} ({browser.Id})");
                    }
                    else if (plugin is IJukeboxVisualizerPlugin visualizer)
                    {
                        await visualizer.InitializeAsync();
                        if (visualizer.IsAvailable)
                        {
                            visualizers.Add(visualizer);
                            Debug.WriteLine($"[PluginLoader] Loaded visualizer: {visualizer.DisplayName} ({visualizer.Id})");
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



    private static Assembly? ResolveFromPluginFolder(ResolveEventArgs args, string pluginFolder)
    {
        // args.Name is the full assembly name; we only need the simple name.
        var simpleName = args.Name?.Split(',')[0];
        if (string.IsNullOrEmpty(simpleName)) return null;

        var dllPath = Path.Combine(pluginFolder, simpleName + ".dll");
        if (!File.Exists(dllPath)) return null;

        try { return Assembly.LoadFrom(dllPath); }
        catch { return null; }
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
