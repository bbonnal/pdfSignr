using Avalonia.Platform.Storage;

namespace pdfSignr.Services;

public class FileDialogService(IWindowAccessor windows) : IFileDialogService
{
    public async Task<string?> PickOpenFileAsync(string title, string[] patterns)
    {
        var files = await windows.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns }]
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension, string[] patterns)
    {
        var file = await windows.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
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
        await windows.Dialog.ShowMessageAsync(title, message);
    }

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        var result = await windows.Dialog.ShowConfirmAsync(title, message);
        return result.Confirmed;
    }
}
