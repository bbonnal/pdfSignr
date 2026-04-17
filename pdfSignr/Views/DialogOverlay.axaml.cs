using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;

namespace pdfSignr.Views;

public enum DialogMode { Message, Confirm, Input, Password }

public class DialogResult
{
    public bool Confirmed { get; init; }
    public string? Text { get; init; }

    public static DialogResult Cancelled => new() { Confirmed = false };
}

public partial class DialogOverlay : UserControl
{
    private TaskCompletionSource<DialogResult>? _tcs;

    public DialogOverlay()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Shows a message with an OK button.</summary>
    public Task<DialogResult> ShowMessageAsync(string title, string message)
        => ShowAsync(DialogMode.Message, title, message);

    /// <summary>Shows a confirmation with OK / Cancel.</summary>
    public Task<DialogResult> ShowConfirmAsync(string title, string message)
        => ShowAsync(DialogMode.Confirm, title, message);

    /// <summary>Shows a text input dialog.</summary>
    public Task<DialogResult> ShowInputAsync(string title, string message, string? placeholder = null)
        => ShowAsync(DialogMode.Input, title, message, placeholder: placeholder);

    /// <summary>Shows a password input dialog.</summary>
    public Task<DialogResult> ShowPasswordAsync(string title, string message, string? errorText = null)
        => ShowAsync(DialogMode.Password, title, message, errorText: errorText);

    private Task<DialogResult> ShowAsync(DialogMode mode, string title, string message,
        string? placeholder = null, string? errorText = null)
    {
        _tcs = new TaskCompletionSource<DialogResult>();

        var titleBlock = this.FindControl<TextBlock>("TitleText")!;
        var messageBlock = this.FindControl<TextBlock>("MessageText")!;
        var errorBlock = this.FindControl<TextBlock>("ErrorText")!;
        var inputBox = this.FindControl<TextBox>("InputBox")!;
        var revealCheck = this.FindControl<CheckBox>("RevealCheck")!;
        var buttonPanel = this.FindControl<StackPanel>("ButtonPanel")!;

        // Reset state
        titleBlock.Text = title;
        messageBlock.Text = message;
        errorBlock.Text = errorText ?? "";
        errorBlock.IsVisible = errorText != null;
        inputBox.Text = "";
        inputBox.IsVisible = false;
        revealCheck.IsVisible = false;
        buttonPanel.Children.Clear();

        // Detach previous handlers
        inputBox.KeyDown -= OnInputKeyDown;
        revealCheck.IsCheckedChanged -= OnRevealChanged;

        switch (mode)
        {
            case DialogMode.Message:
                AddButton(buttonPanel, "OK", true);
                break;

            case DialogMode.Confirm:
                AddButton(buttonPanel, "Cancel", false);
                AddButton(buttonPanel, "OK", true);
                break;

            case DialogMode.Input:
                inputBox.IsVisible = true;
                inputBox.PasswordChar = '\0';
                inputBox.PlaceholderText = placeholder ?? "";
                inputBox.KeyDown += OnInputKeyDown;
                AddButton(buttonPanel, "Cancel", false);
                AddButton(buttonPanel, "OK", true);
                break;

            case DialogMode.Password:
                inputBox.IsVisible = true;
                inputBox.PasswordChar = '\u2022';
                inputBox.PlaceholderText = "Password";
                inputBox.KeyDown += OnInputKeyDown;
                revealCheck.IsVisible = true;
                revealCheck.IsChecked = false;
                revealCheck.IsCheckedChanged += OnRevealChanged;
                AddButton(buttonPanel, "Cancel", false);
                AddButton(buttonPanel, "OK", true);
                break;
        }

        IsVisible = true;

        if (mode is DialogMode.Input or DialogMode.Password)
            inputBox.Focus();

        return _tcs.Task;
    }

    private void AddButton(StackPanel panel, string text, bool isConfirm)
    {
        var btn = new Button
        {
            Content = text,
            Width = 80,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        btn.Click += (_, _) => Close(isConfirm);
        panel.Children.Add(btn);
    }

    private void Close(bool confirmed)
    {
        var inputBox = this.FindControl<TextBox>("InputBox")!;
        var revealCheck = this.FindControl<CheckBox>("RevealCheck")!;

        // For input/password modes, require non-empty text on confirm
        if (confirmed && inputBox.IsVisible && string.IsNullOrWhiteSpace(inputBox.Text))
            return;

        inputBox.KeyDown -= OnInputKeyDown;
        revealCheck.IsCheckedChanged -= OnRevealChanged;
        IsVisible = false;

        var result = confirmed
            ? new DialogResult { Confirmed = true, Text = inputBox.IsVisible ? inputBox.Text : null }
            : DialogResult.Cancelled;

        _tcs?.TrySetResult(result);
        _tcs = null;
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Close(true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close(false);
            e.Handled = true;
        }
    }

    private void OnRevealChanged(object? sender, RoutedEventArgs e)
    {
        var inputBox = this.FindControl<TextBox>("InputBox")!;
        var revealCheck = this.FindControl<CheckBox>("RevealCheck")!;
        inputBox.PasswordChar = revealCheck.IsChecked == true ? '\0' : '\u2022';
    }
}
