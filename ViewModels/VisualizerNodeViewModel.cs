using CommunityToolkit.Mvvm.ComponentModel;
using Jukebox.Services;
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
            try
            {
                var normFilePath = System.IO.Path.GetFullPath(Path);
                var dir = System.IO.Path.GetDirectoryName(normFilePath);
                while (!string.IsNullOrEmpty(dir))
                {
                    if (string.Equals(System.IO.Path.GetFileName(dir), "Favorites", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                    dir = System.IO.Path.GetDirectoryName(dir);
                }
                return false;
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

