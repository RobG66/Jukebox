using System.Threading.Tasks;
using Jukebox.Views;

namespace Jukebox.Services;

/// <summary>
/// Default implementation of <see cref="IUserDialogService"/> that delegates
/// to the existing <c>ThreeButtonDialogView</c> static methods.
/// </summary>
/// <remarks>
/// This is a thin adapter so services don't reference <c>Jukebox.Views</c>
/// directly. The View layer is still the one showing the dialog; the service
/// just doesn't know that.
/// </remarks>
public sealed class UserDialogService : IUserDialogService
{
    public Task ShowErrorAsync(string title, string message)
        => ThreeButtonDialogView.ShowErrorAsync(title, message);

    public Task ShowWarningAsync(string title, string message)
        => ThreeButtonDialogView.ShowWarningAsync(title, message);

    public Task ShowInfoAsync(string title, string message)
        => ThreeButtonDialogView.ShowInfoAsync(title, message);

    public Task<bool> ShowConfirmAsync(string title, string message)
        => ThreeButtonDialogView.ShowConfirmAsync(title, message);
}
