using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;
using System.Threading.Tasks;

namespace Jukebox.Views;

public partial class TextInputDialogView : Window
{
    private readonly Func<string, (bool IsValid, string ErrorMessage)>? _validator;

    public static async Task<string?> ShowAsync(string title, string prompt, string placeholder = "", Func<string, (bool IsValid, string ErrorMessage)>? validator = null, string okButtonText = "OK", Window? owner = null)
    {
        owner ??= (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner == null) return null;

        var dialog = new TextInputDialogView(title, prompt, placeholder, validator, okButtonText);
        return await dialog.ShowDialog<string?>(owner);
    }

    public TextInputDialogView() : this("Dialog", "Enter input:", string.Empty, null, "OK") { }

    public TextInputDialogView(string title, string prompt, string placeholder, Func<string, (bool IsValid, string ErrorMessage)>? validator = null, string okButtonText = "OK")
    {
        InitializeComponent();
        Title = title;
        PromptTextBlock.Text = prompt;
        InputTextBox.PlaceholderText = placeholder;
        OkButton.Content = okButtonText;
        _validator = validator;

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
        Close(null);
    }

    private void InputTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
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
            Close(null);
            e.Handled = true;
        }
    }

    private void Submit()
    {
        string text = InputTextBox.Text?.Trim() ?? string.Empty;
        if (Validate(text, out string errorMessage))
        {
            Close(text);
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
