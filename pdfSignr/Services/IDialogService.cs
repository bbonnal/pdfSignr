namespace pdfSignr.Services;

/// <summary>
/// Abstracts modal dialogs (password, message, confirmation) behind a ViewModel-friendly API,
/// so ViewModels never reference View-layer overlays directly.
/// </summary>
public interface IDialogService
{
    Task<string?> ShowPasswordAsync(string title, string message, bool showError);
    Task ShowMessageAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message);
}
