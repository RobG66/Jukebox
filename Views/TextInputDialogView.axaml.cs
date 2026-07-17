using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Jukebox.Views;

public partial class TextInputDialogView : Window
{
    private readonly Func<string, (bool IsValid, string ErrorMessage)>? _validator;

    /// <summary>
    /// Shows a text-input dialog. When <paramref name="showDefaultCheckbox"/>
    /// is true, a "Save as default startup playlist" checkbox is shown below the
    /// text input. If the user checks it, the text box is disabled and the
    /// returned name is "Default".
    /// </summary>
    public static async Task<(string? Name, bool SaveAsDefault)> ShowAsync(
        string title,
        string prompt,
        string placeholder = "",
        Func<string, (bool IsValid, string ErrorMessage)>? validator = null,
        string okButtonText = "OK",
        bool showDefaultCheckbox = false,
        string defaultCheckboxText = "Save as default startup playlist",
        Window? owner = null)
    {
        if (owner == null)
        {
            var desktop = Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (desktop != null)
            {
                owner = desktop.Windows.FirstOrDefault(w => w.IsActive)
                        ?? desktop.Windows.LastOrDefault(w => w.IsVisible && w is not TextInputDialogView)
                        ?? desktop.MainWindow;
            }
        }
        if (owner == null) return (null, false);

        var dialog = new TextInputDialogView(title, prompt, placeholder, validator, okButtonText, showDefaultCheckbox, defaultCheckboxText);
        var result = await dialog.ShowDialog<(string? name, bool saveAsDefault)>(owner);
        return result;
    }

    public TextInputDialogView() : this("Dialog", "Enter input:", string.Empty, null, "OK", false, "") { }

    public TextInputDialogView(
        string title,
        string prompt,
        string placeholder,
        Func<string, (bool IsValid, string ErrorMessage)>? validator = null,
        string okButtonText = "OK",
        bool showDefaultCheckbox = false,
        string defaultCheckboxText = "")
    {
        InitializeComponent();
        Title = title;
        PromptTextBlock.Text = prompt;
        InputTextBox.PlaceholderText = placeholder;
        OkButton.Content = okButtonText;
        _validator = validator;

        if (showDefaultCheckbox)
        {
            DefaultCheckBox.Content = string.IsNullOrEmpty(defaultCheckboxText)
                ? "Save as default startup playlist"
                : defaultCheckboxText;
            DefaultCheckBox.IsVisible = true;
        }

        Loaded += (s, e) =>
        {
            InputTextBox.Focus();
        };
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        Submit();
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(((string?)null, false));
    }

    private void InputTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        ErrorTextBlock.IsVisible = false;
    }

    // When the "save as default" checkbox is toggled, enable/disable the text
    // input. When checked, the name is forced to "Default" and validation is
    // skipped — "Default" is always a valid playlist name.
    private void DefaultCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        bool isChecked = DefaultCheckBox.IsChecked == true;
        InputTextBox.IsEnabled = !isChecked;
        ErrorTextBlock.IsVisible = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Enter)
        {
            Submit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close(((string?)null, false));
            e.Handled = true;
        }
    }

    private void Submit()
    {
        // If the "save as default" checkbox is checked, the name is always
        // "Default" — skip validation and return immediately.
        if (DefaultCheckBox.IsVisible && DefaultCheckBox.IsChecked == true)
        {
            Close(("Default", true));
            return;
        }

        string text = InputTextBox.Text?.Trim() ?? string.Empty;
        if (Validate(text, out string errorMessage))
        {
            Close((text, false));
        }
        else
        {
            ErrorTextBlock.Text = errorMessage;
            ErrorTextBlock.IsVisible = true;
        }
    }

    private bool Validate(string text, out string errorMessage)
    {
        errorMessage = string.Empty;

        if (_validator != null)
        {
            var result = _validator(text);
            errorMessage = result.ErrorMessage;
            return result.IsValid;
        }

        return true;
    }
}
