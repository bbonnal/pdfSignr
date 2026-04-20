namespace pdfSignr.Services;

public class DialogService : IDialogService
{
    private readonly IWindowAccessor _windows;

    public DialogService(IWindowAccessor windows)
    {
        _windows = windows;
    }

    public async Task<string?> ShowPasswordAsync(string title, string message, bool showError)
    {
        var errorText = showError ? "Incorrect password. Please try again." : null;
        var result = await _windows.Dialog.ShowPasswordAsync(title, message, errorText);
        return result.Confirmed ? result.Text : null;
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        await _windows.Dialog.ShowMessageAsync(title, message);
    }

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        var result = await _windows.Dialog.ShowConfirmAsync(title, message);
        return result.Confirmed;
    }
}
