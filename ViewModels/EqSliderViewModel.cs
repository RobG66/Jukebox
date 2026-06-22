using CommunityToolkit.Mvvm.ComponentModel;

namespace Jukebox.ViewModels;

public partial class EqSliderViewModel : ViewModelBase
{
    [ObservableProperty] private double _gain;
    [ObservableProperty] private string _frequencyLabel = "";
    [ObservableProperty] private float _centerFrequency;
}
