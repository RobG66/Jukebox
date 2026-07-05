using System.Threading.Tasks;

namespace Jukebox.Services;

/// <summary>
/// Abstraction over user-facing dialogs (errors, confirmations, prompts).
/// </summary>
/// <remarks>
/// Services that need to surface errors to the user depend on this interface
/// rather than calling <c>ThreeButtonDialogView.ShowErrorAsync</c> directly.
/// This breaks the previous services → views dependency inversion, and makes
/// services testable without a live Avalonia dispatcher.
///
/// The default implementation (<see cref="UserDialogService"/>) delegates to
/// the existing <c>ThreeButtonDialogView</c> static methods.
/// </remarks>
public interface IUserDialogService
{
    /// <summary>Show a modal error dialog with an OK button.</summary>
    Task ShowErrorAsync(string title, string message);

    /// <summary>Show a modal warning dialog with an OK button.</summary>
    Task ShowWarningAsync(string title, string message);

    /// <summary>Show a modal info dialog with an OK button.</summary>
    Task ShowInfoAsync(string title, string message);

    /// <summary>Show a modal confirmation dialog with Yes/No buttons. Returns true if the user clicks Yes.</summary>
    Task<bool> ShowConfirmAsync(string title, string message);
}
