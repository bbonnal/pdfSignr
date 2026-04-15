namespace pdfSignr.Services;

public interface IFileDialogService
{
    Task<string?> PickOpenFileAsync(string title, string[] patterns);
    Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension, string[] patterns);
}
