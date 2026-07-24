using System;
using System.Threading.Tasks;
using Jukebox.Models;

namespace Jukebox.Services;

/// <summary>
/// Abstraction over user-facing dialogs (errors, confirmations, prompts).
/// </summary>
/// <remarks>
/// Services and ViewModels depend on this interface rather than calling
/// <c>ThreeButtonDialogView.ShowErrorAsync</c> etc. directly. This breaks
/// the ViewModels → Views dependency and makes VMs testable without a
/// live Avalonia dispatcher.
///
/// The default implementation (<see cref="UserDialogService"/>) delegates
/// to the existing <c>ThreeButtonDialogView</c> / <c>TextInputDialogView</c>
/// / <c>RenameDialogView</c> static methods.
/// </remarks>
public interface IUserDialogService
{
    /// <summary>Show a modal error dialog with an OK button.</summary>
    Task ShowErrorAsync(string title, string message);

    /// <summary>Show a modal warning dialog with an OK button.</summary>
    Task ShowWarningAsync(string title, string message);

    /// <summary>Show a modal info dialog with an OK button.</summary>
    Task ShowInfoAsync(string title, string message);

    /// <summary>
    /// Show a modal confirmation dialog with Yes/No-style buttons.
    /// Returns true if the user clicks the confirm button.
    /// </summary>
    /// <param name="title">Window title.</param>
    /// <param name="message">Main message text.</param>
    /// <param name="confirmText">Text on the confirm button (default "Yes").</param>
    /// <param name="cancelText">Text on the cancel button (default "Cancel").</param>
    /// <param name="icon">Icon theme (default Question).</param>
    /// <param name="detail">Optional secondary detail text shown below the message.</param>
    Task<bool> ShowConfirmAsync(
        string title,
        string message,
        string confirmText = "Yes",
        string cancelText = "Cancel",
        DialogIconTheme icon = DialogIconTheme.Question,
        string? detail = null);

    /// <summary>
    /// Show a modal text-input dialog with an OK/Cancel pair. Returns the
    /// trimmed text on OK, or null if the user cancelled.
    /// </summary>
    /// <param name="title">Window title.</param>
    /// <param name="prompt">Label above the text box.</param>
    /// <param name="placeholder">Placeholder text in the input box.</param>
    /// <param name="validator">Optional validator; returns (IsValid, ErrorMessage).</param>
    /// <param name="okButtonText">Text on the OK button.</param>
    /// <param name="showDefaultCheckbox">If true, show a "save as default" checkbox.</param>
    /// <param name="defaultCheckboxText">Label for the default checkbox.</param>
    /// <returns>(Name, SaveAsDefault). Name is null if cancelled.</returns>
    Task<(string? Name, bool SaveAsDefault)> ShowTextInputAsync(
        string title,
        string prompt,
        string placeholder = "",
        Func<string, (bool IsValid, string ErrorMessage)>? validator = null,
        string okButtonText = "OK",
        bool showDefaultCheckbox = false,
        string defaultCheckboxText = "Save as default startup playlist");

    /// <summary>
    /// Show a modal rename dialog pre-populated with the current name.
    /// Returns the new name on confirm, or null if cancelled.
    /// </summary>
    /// <param name="currentName">The name to pre-populate and select.</param>
    /// <param name="validator">Optional validator; returns (IsValid, ErrorMessage).</param>
    /// <param name="title">Optional dialog title.</param>
    /// <param name="prompt">Optional prompt label text.</param>
    /// <param name="isFileName">If true, enforces invalid file name character checks.</param>
    Task<string?> ShowRenameAsync(
        string currentName,
        Func<string, (bool IsValid, string ErrorMessage)>? validator = null,
        string title = "Rename Profile",
        string prompt = "Enter new name for the profile:",
        bool isFileName = true);
}
