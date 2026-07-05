using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Jukebox.Services;

public class StorageService : IStorageService
{
    private readonly Window _mainWindow;

    public StorageService(Window mainWindow)
    {
        _mainWindow = mainWindow;
    }

    public async Task<List<string>> OpenFileDialogAsync(string title, bool allowMultiple, string[]? audioExtensions = null, string[]? videoExtensions = null)
    {
        var patterns = new List<string>();
        if (audioExtensions != null) patterns.AddRange(audioExtensions.Select(e => $"*{e}"));
        if (videoExtensions != null) patterns.AddRange(videoExtensions.Select(e => $"*{e}"));

        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple
        };

        if (patterns.Count > 0)
        {
            options.FileTypeFilter = new[]
            {
                new FilePickerFileType("Media Files")
                {
                    Patterns = patterns
                }
            };
        }

        var files = await _mainWindow.StorageProvider.OpenFilePickerAsync(options);
        return files?.Select(f => f.TryGetLocalPath() ?? string.Empty)
                     .Where(p => !string.IsNullOrEmpty(p))
                     .ToList() ?? new List<string>();
    }

    public async Task<string?> OpenFolderDialogAsync(string title)
    {
        var folders = await _mainWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title
        });

        if (folders == null || folders.Count == 0) return null;
        return folders[0].TryGetLocalPath();
    }
}
