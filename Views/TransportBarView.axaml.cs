using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Jukebox.Views;

public partial class TransportBarView : UserControl
{
    public TransportBarView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
