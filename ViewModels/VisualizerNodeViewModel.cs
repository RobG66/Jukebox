using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace Jukebox.ViewModels;

public abstract partial class VisualizerNodeViewModel : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _path = string.Empty;

    // Helps TreeDataGrid differentiate between expandable nodes and leaf nodes
    public bool IsDirectory => this is VisualizerFolderViewModel;
    public bool IsFile => this is VisualizerFileViewModel;
    public bool IsFavorite
    {
        get
        {
            if (!IsFile || string.IsNullOrEmpty(Path)) return false;
            var favFolder = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectM", "Presets", "Favorites");
            try
            {
                var normFilePath = System.IO.Path.GetFullPath(Path);
                var normFavFolder = System.IO.Path.GetFullPath(favFolder) + System.IO.Path.DirectorySeparatorChar;
                return normFilePath.StartsWith(normFavFolder, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    // Command availability checks
    public bool CanAddToFavorites => IsFile && !IsFavorite;
    public bool CanRemoveFromFavorites => IsFile && IsFavorite;
    public bool CanRename => IsFile;

    protected VisualizerNodeViewModel(string name, string path)
    {
        Name = name;
        Path = path;
    }
}

public partial class VisualizerFolderViewModel : VisualizerNodeViewModel
{
    public ObservableCollection<VisualizerNodeViewModel> Children { get; } = new();

    public VisualizerFolderViewModel(string name, string path) : base(name, path)
    {
    }
}

public partial class VisualizerFileViewModel : VisualizerNodeViewModel
{
    public VisualizerFileViewModel(string name, string path) : base(name, path)
    {
    }
}
