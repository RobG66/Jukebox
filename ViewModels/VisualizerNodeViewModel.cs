using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace Jukebox.ViewModels;

public abstract partial class VisualizerNodeViewModel : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _path = string.Empty;

    // Helps TreeDataGrid differentiate between expandable nodes and leaf nodes
    public bool IsDirectory => this is VisualizerFolderViewModel;

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
