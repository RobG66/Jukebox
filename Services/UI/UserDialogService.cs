using System;
using System.Threading.Tasks;
using Jukebox.Views;

namespace Jukebox.Services;

/// <summary>
/// Default implementation of <see cref="IUserDialogService"/> that delegates
/// to the existing <c>ThreeButtonDialogView</c> / <c>TextInputDialogView</c>
/// / <c>RenameDialogView</c> static methods.
/// </summary>
/// <remarks>
/// This is a thin adapter so services and VMs don't reference
/// <c>Jukebox.Views</c> directly. The View layer is still the one showing
/// the dialog; the service just doesn't know that.
/// </remarks>
public sealed class UserDialogService : IUserDialogService
{
    public Task ShowErrorAsync(string title, string message)
        => ThreeButtonDialogView.ShowErrorAsync(title, message);

    public Task ShowWarningAsync(string title, string message)
        => ThreeButtonDialogView.ShowWarningAsync(title, message);

    public Task ShowInfoAsync(string title, string message)
        => ThreeButtonDialogView.ShowInfoAsync(title, message);

    public Task<bool> ShowConfirmAsync(
        string title,
        string message,
        string confirmText = "Yes",
        string cancelText = "Cancel",
        Models.DialogIconTheme icon = Models.DialogIconTheme.Question,
        string? detail = null)
        => ThreeButtonDialogView.ShowConfirmAsync(title, message, confirmText, cancelText, icon, detail);

    public Task<(string? Name, bool SaveAsDefault)> ShowTextInputAsync(
        string title,
        string prompt,
        string placeholder = "",
        Func<string, (bool IsValid, string ErrorMessage)>? validator = null,
        string okButtonText = "OK",
        bool showDefaultCheckbox = false,
        string defaultCheckboxText = "Save as default startup playlist")
        => TextInputDialogView.ShowAsync(title, prompt, placeholder, validator, okButtonText, showDefaultCheckbox, defaultCheckboxText);

    public Task<string?> ShowRenameAsync(
        string currentName,
        Func<string, (bool IsValid, string ErrorMessage)>? validator = null,
        string title = "Rename Profile",
        string prompt = "Enter new name for the profile:",
        bool isFileName = true)
        => RenameDialogView.ShowAsync(currentName, validator: validator, title: title, prompt: prompt, isFileName: isFileName);
}
