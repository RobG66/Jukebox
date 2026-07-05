using System.Collections.Generic;
using System.Threading.Tasks;

namespace Jukebox.Services;

public interface IStorageService
{
    Task<List<string>> OpenFileDialogAsync(string title, bool allowMultiple, string[]? audioExtensions = null, string[]? videoExtensions = null);
    Task<string?> OpenFolderDialogAsync(string title);
}
