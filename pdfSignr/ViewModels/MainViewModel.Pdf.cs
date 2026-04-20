using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using pdfSignr.Models;
using pdfSignr.Services;

namespace pdfSignr.ViewModels;

// PDF loading, saving, and password handling
public partial class MainViewModel
{
    [RelayCommand]
    private async Task Open()
    {
        var path = await _fileDialogs.PickOpenFileAsync("Open PDF", ["*.pdf"]);
        if (path == null) return;
        await LoadPdfAsync(path);
    }

    public async Task LoadPdfAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                BaseStatus = $"File not found: {Path.GetFileName(path)}";
                return;
            }

            var fileName = Path.GetFileName(path);
            BaseStatus = $"Opening {fileName}\u2026";

            var newPages = await RunWithPasswordRetryAsync(
                pw => LoadPdfCore(path, pw), fileName, "Open");
            if (newPages == null) return;

            // Dispose old pages, then swap the entire collection in one shot
            foreach (var page in Pages)
                page.DisposeResources();

            PdfFilePath = path;
            SelectAnnotation(null);
            Pages = newPages;
            UndoRedo.Clear();

            BaseStatus = $"{fileName} \u2014 {newPages.Count} page{(newPages.Count != 1 ? "s" : "")}";
            PdfLoaded?.Invoke();
        }
        catch (Exception ex)
        {
            BaseStatus = $"Failed to open: {ex.Message}";
            _ = _fileDialogs.ShowErrorAsync("Failed to Open", ex.Message);
        }
    }

    /// <summary>
    /// Runs a function that may require a PDF password, retrying with user prompts on password errors.
    /// Returns null if the user cancels or max attempts exceeded.
    /// </summary>
    private async Task<T?> RunWithPasswordRetryAsync<T>(
        Func<string?, T> work, string fileName, string verb) where T : class
    {
        string? password = null;
        bool showError = false;
        int attempts = 0;
        const int maxAttempts = 5;

        while (true)
        {
            try
            {
                return await Task.Run(() => work(password));
            }
            catch (Exception ex) when (IsPasswordError(ex))
            {
                if (++attempts > maxAttempts)
                {
                    BaseStatus = "Too many failed password attempts";
                    return null;
                }
                var pw = await _dialogs.ShowPasswordAsync(
                    "Unlock PDF",
                    $"\"{fileName}\" is password-protected.",
                    showError);
                if (pw == null)
                {
                    BaseStatus = $"{verb} cancelled";
                    return null;
                }
                password = pw;
                showError = true;
            }
        }
    }

    private static bool IsPasswordError(Exception? ex)
    {
        // pdfium (PDFtoImage) wraps errors — walk the full exception chain
        while (ex != null)
        {
            var msg = ex.Message;
            if (msg.Contains("password", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("encrypted", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("FPDF_ERR_PASSWORD", StringComparison.OrdinalIgnoreCase))
                return true;
            ex = ex.InnerException;
        }
        return false;
    }

    private PageItem[] CreatePageItems(byte[] pdfBytes, int startIndex = 0, string? password = null)
    {
        var sizes = _renderService.GetAllPageSizes(pdfBytes, password);
        int count = sizes.Count;

        var items = new PageItem[count];
        for (int i = 0; i < count; i++)
        {
            var (w, h) = sizes[i];
            items[i] = new PageItem
            {
                Index = startIndex + i,
                Bitmap = null, OriginalWidthPt = w, OriginalHeightPt = h,
                Source = new PageSource(pdfBytes, i, password),
                ParentVM = this
            };
        }
        return items;
    }

    private ObservableCollection<PageItem> LoadPdfCore(string path, string? password = null)
    {
        var items = CreatePageItems(File.ReadAllBytes(path), password: password);
        int count = items.Length;
        for (int i = 0; i < count; i++)
        {
            items[i].DisplayNumber = i + 1;
            items[i].IsFirst = i == 0;
        }
        return new ObservableCollection<PageItem>(items);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        var defaultName = PdfFilePath != null
            ? Path.GetFileNameWithoutExtension(PdfFilePath) + "_signed"
            : "document_signed";
        await SavePagesCore(Pages, "Save Annotated PDF", defaultName,
            file => $"Saved to {file}");
    }

    private bool CanSave => Pages.Count > 0;

    private async Task SavePagesCore(
        IEnumerable<PageItem> pages,
        string dialogTitle,
        string defaultName,
        Func<string, string> formatSuccess,
        string errorVerb = "Save")
    {
        var (cancelled, outPw) = await GetOutputPasswordAsync();
        if (cancelled) return;

        var outputPath = await _fileDialogs.PickSaveFileAsync(dialogTitle, defaultName, ".pdf", ["*.pdf"]);
        if (outputPath == null) return;

        var pagesWithAnnotations = pages
            .Select(p => (p.Source, p.RotationDegrees, (IEnumerable<Annotation>)p.Annotations));

        var progress = new Progress<int>(p => BaseStatus = $"Saving\u2026 {p}%");
        try
        {
            await _saveService.SaveAsync(outputPath, pagesWithAnnotations, outPw, progress);
            BaseStatus = formatSuccess(Path.GetFileName(outputPath));
        }
        catch (Exception ex)
        {
            BaseStatus = $"{errorVerb} failed: {ex.Message}";
            await _fileDialogs.ShowErrorAsync($"{errorVerb} Failed", ex.Message);
        }
    }

    /// <summary>Saves a subset of pages. rangeSpec format: "3-7" (1-based inclusive).</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SavePageRange(string rangeSpec)
    {
        var parts = rangeSpec.Split('-');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out int from) ||
            !int.TryParse(parts[1], out int to)) return;
        if (from < 1 || to > Pages.Count || from > to) return;

        var baseName = PdfFilePath != null
            ? Path.GetFileNameWithoutExtension(PdfFilePath)
            : "document";
        var suffix = from == to ? $"_p{from}" : $"_p{from}-{to}";

        var (cancelled, outPw) = await GetOutputPasswordAsync();
        if (cancelled) return;

        var outputPath = await _fileDialogs.PickSaveFileAsync(
            $"Save pages {from}\u2013{to}", baseName + suffix, ".pdf", ["*.pdf"]);
        if (outputPath == null) return;

        var progress = new Progress<int>(p => BaseStatus = $"Saving\u2026 {p}%");
        try
        {
            var pagesInRange = Pages.Skip(from - 1).Take(to - from + 1)
                .Select(p => (p.Source, p.RotationDegrees, (IEnumerable<Annotation>)p.Annotations));
            await _saveService.SaveAsync(outputPath, pagesInRange, outPw, progress);
            BaseStatus = from == to
                ? $"Page {from} saved to {Path.GetFileName(outputPath)}"
                : $"Pages {from}\u2013{to} saved to {Path.GetFileName(outputPath)}";
        }
        catch (Exception ex)
        {
            BaseStatus = $"Save failed: {ex.Message}";
            await _fileDialogs.ShowErrorAsync("Save Failed", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedPages))]
    private async Task SaveSelectedPages()
    {
        var selected = SelectedPages;
        var defaultName = PdfFilePath != null
            ? Path.GetFileNameWithoutExtension(PdfFilePath) + "_selected"
            : "document_selected";
        await SavePagesCore(selected, "Save Selected Pages", defaultName,
            file => $"Saved {selected.Count} page{(selected.Count != 1 ? "s" : "")} to {file}");
    }

}
