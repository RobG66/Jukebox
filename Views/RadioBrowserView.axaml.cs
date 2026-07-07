using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Jukebox.ViewModels;

namespace Jukebox.Views;

public partial class RadioBrowserView : Window
{
    public RadioBrowserView()
    {
        InitializeComponent();
    }

    public RadioBrowserView(RadioBrowserViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void DataGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is RadioBrowserViewModel vm && vm.PlaySelectedCommand.CanExecute(null))
        {
            vm.PlaySelectedCommand.Execute(null);
        }
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
