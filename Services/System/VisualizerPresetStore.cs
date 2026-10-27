using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Jukebox.Services;

/// <summary>Persists original preset paths without staging preset or texture files.</summary>
internal sealed class VisualizerPresetStore
{
    private readonly string _settingsFile;
    private readonly string _appDirectory;
    private readonly object _saveLock = new();
    private Task _pendingSave = Task.CompletedTask;

    public VisualizerPresetStore(IPathProvider paths)
    {
        _settingsFile = Path.Combine(paths.SettingsDirectory, "visualizer-presets.json");
        _appDirectory = Path.GetFullPath(Path.Combine(paths.SettingsDirectory, ".."));
    }

    public async Task<string?> RestoreAsync(string pluginId, string? legacyDirectory, IReadOnlyList<string> presets)
    {
        Task pendingSave;
        lock (_saveLock) pendingSave = _pendingSave;
        await pendingSave.ConfigureAwait(false);
        try
        {
            return await RestoreCoreAsync(pluginId, legacyDirectory, presets).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Debug.WriteLine($"[Visualizer] Failed restoring current preset: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> RestoreCoreAsync(string pluginId, string? legacyDirectory, IReadOnlyList<string> presets)
    {
        var settings = await ReadAsync().ConfigureAwait(false);
        if (settings.TryGetValue(pluginId, out var savedPath))
        {
            var fullPath = Path.GetFullPath(savedPath, _appDirectory);
            // A missing saved preset falls back to an available preset, rather
            // than reviving an obsolete copy from the old staging directory.
            return await Task.Run(() => File.Exists(fullPath) ? fullPath : null).ConfigureAwait(false);
        }

        // One-time migration: locate the original of the old startup copy.
        // Compare contents too, since different packs can share preset names.
        return await Task.Run(async () =>
        {
            if (string.IsNullOrEmpty(legacyDirectory) || !Directory.Exists(legacyDirectory)) return null;
            foreach (var copy in Directory.EnumerateFiles(legacyDirectory, "*.milk"))
            {
                var candidates = presets.Where(path => string.Equals(Path.GetFileName(path),
                    Path.GetFileName(copy), StringComparison.OrdinalIgnoreCase)).ToList();
                if (candidates.Count == 0) continue;
                string content = await File.ReadAllTextAsync(copy).ConfigureAwait(false);
                foreach (var candidate in candidates)
                {
                    if (await File.ReadAllTextAsync(candidate).ConfigureAwait(false) == content)
                        return candidate;
                }
            }
            return null;
        }).ConfigureAwait(false);
    }

    public Task SaveAsync(string pluginId, string presetPath)
    {
        lock (_saveLock)
        {
            _pendingSave = SaveAfterAsync(_pendingSave, pluginId, presetPath);
            return _pendingSave;
        }
    }

    private async Task SaveAfterAsync(Task previous, string pluginId, string presetPath)
    {
        await previous.ConfigureAwait(false);
        try
        {
            var settings = await ReadAsync().ConfigureAwait(false);
            settings[pluginId] = Path.GetRelativePath(_appDirectory, presetPath).Replace(Path.DirectorySeparatorChar, '/');
            await Task.Run(async () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
                string temporaryFile = _settingsFile + ".tmp";
                await File.WriteAllTextAsync(temporaryFile, JsonSerializer.Serialize(settings)).ConfigureAwait(false);
                File.Move(temporaryFile, _settingsFile, overwrite: true);
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Debug.WriteLine($"[Visualizer] Failed saving current preset: {ex.Message}");
        }
    }

    private async Task<Dictionary<string, string>> ReadAsync()
    {
        try
        {
            string json = await File.ReadAllTextAsync(_settingsFile).ConfigureAwait(false);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"[Visualizer] Failed reading current preset: {ex.Message}");
            return new();
        }
    }
}
