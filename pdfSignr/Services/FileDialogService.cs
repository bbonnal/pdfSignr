using Avalonia.Platform.Storage;
using pdfSignr.Views;

namespace pdfSignr.Services;

public class FileDialogService(IStorageProvider storageProvider, DialogOverlay dialog) : IFileDialogService
{
    public async Task<string?> PickOpenFileAsync(string title, string[] patterns)
    {
        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns }]
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension, string[] patterns)
    {
        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(title) { Patterns = patterns }]
        });
        return file?.TryGetLocalPath();
    }

    public async Task ShowErrorAsync(string title, string message)
    {
        await dialog.ShowMessageAsync(title, message);
    }

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        var result = await dialog.ShowConfirmAsync(title, message);
        return result.Confirmed;
    }
}
