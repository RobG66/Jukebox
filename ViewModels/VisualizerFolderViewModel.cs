using System.Collections.ObjectModel;

namespace Jukebox.ViewModels;

public partial class VisualizerFolderViewModel : VisualizerNodeViewModel
{
    public ObservableCollection<VisualizerNodeViewModel> Children { get; } = new();

    public VisualizerFolderViewModel(string name, string path) : base(name, path)
    {
    }
}
