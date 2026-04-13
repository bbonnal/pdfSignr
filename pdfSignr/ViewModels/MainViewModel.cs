using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using pdfSignr.Models;
using pdfSignr.Services;

namespace pdfSignr.ViewModels;

public partial class PageItem : ObservableObject
{
    [ObservableProperty] private int _index;
    [ObservableProperty] private Bitmap? _bitmap;
    [ObservableProperty] private bool _isFirst;
    [ObservableProperty] private bool _isLast;
    public double WidthPt { get; init; }
    public double HeightPt { get; init; }
    public PageSource Source { get; init; } = null!;
    public ObservableCollection<Annotation> Annotations { get; } = new();
}

public partial class MainViewModel : ObservableObject
{
    public const int RenderDpi = 150;

    private readonly Window _window;
    private IStorageProvider Storage => _window.StorageProvider;

    [ObservableProperty] private string? _pdfFilePath;
    [ObservableProperty] private ObservableCollection<PageItem> _pages = new();
    [ObservableProperty] private Annotation? _selectedAnnotation;
    [ObservableProperty] private string _currentTool = "Select";
    private string _baseStatus = "Open a PDF to get started";
    [ObservableProperty] private string _statusText = "Open a PDF to get started";
    [ObservableProperty] private string? _signatureSvgPath;

    public static string[] AvailableFonts { get; } = ["Helvetica", "Times-Roman", "Courier"];

    // Set by MainWindow when zoom changes
    public int ZoomPercent { get; set; } = 100;

    // Computed tool-active properties for Ribbon ToggleButton binding
    public bool IsTextToolActive => CurrentTool == "Text";
    public bool IsSignToolActive => CurrentTool == "Signature";
    public string TextToolTip => IsTextToolActive ? "Click on page to place text" : "Add text";
    public string SignToolTip => IsSignToolActive ? "Click on page to place signature" : "Add signature";
    public Cursor? PlacementCursor => CurrentTool is "Text" or "Signature"
        ? new Cursor(StandardCursorType.DragMove) : null;

    public event Action? PdfLoaded;

    public MainViewModel(Window window)
    {
        _window = window;
        Pages.CollectionChanged += OnPagesCollectionChanged;
    }

    private void OnPagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SaveCommand.NotifyCanExecuteChanged();
        CompressResampleCommand.NotifyCanExecuteChanged();
        CompressRasterizeCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedAnnotationChanged(Annotation? oldValue, Annotation? newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= OnAnnotationPropChanged;
        if (newValue != null) newValue.PropertyChanged += OnAnnotationPropChanged;
        DeleteCommand.NotifyCanExecuteChanged();
        UpdateStatusText();
    }

    private void OnAnnotationPropChanged(object? sender, PropertyChangedEventArgs e) => UpdateStatusText();

    public void UpdateStatusText()
    {
        var info = SelectedAnnotation switch
        {
            TextAnnotation t => $"  \u2502  {t.FontFamily}  {t.FontSize:F1} pt" +
                                (t.Rotation != 0 ? $"  {t.Rotation:F0}\u00b0" : ""),
            SvgAnnotation s => $"  \u2502  {s.WidthPt:F0} \u00d7 {s.HeightPt:F0} pt" +
                               (s.Rotation != 0 ? $"  {s.Rotation:F0}\u00b0" : ""),
            _ => ""
        };
        StatusText = $"{_baseStatus}{info}  \u2502  {ZoomPercent}%";
    }

    partial void OnPdfFilePathChanged(string? value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        CompressResampleCommand.NotifyCanExecuteChanged();
        CompressRasterizeCommand.NotifyCanExecuteChanged();
    }

    partial void OnCurrentToolChanged(string value)
    {
        OnPropertyChanged(nameof(IsTextToolActive));
        OnPropertyChanged(nameof(IsSignToolActive));
        OnPropertyChanged(nameof(TextToolTip));
        OnPropertyChanged(nameof(SignToolTip));
        OnPropertyChanged(nameof(PlacementCursor));
    }

    // --- Commands ---

    [RelayCommand]
    private async Task Open()
    {
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open PDF",
            FileTypeFilter = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }]
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        LoadPdf(path);
    }

    public void LoadPdf(string path)
    {
        foreach (var page in Pages)
        {
            page.Bitmap?.Dispose();
            foreach (var ann in page.Annotations)
                if (ann is SvgAnnotation svg) svg.RenderedBitmap?.Dispose();
        }

        PdfFilePath = path;
        Pages.Clear();
        SelectAnnotation(null);

        var pdfBytes = File.ReadAllBytes(path);
        var pageCount = PdfRenderService.GetPageCount(pdfBytes);

        for (int i = 0; i < pageCount; i++)
        {
            var (w, h) = PdfRenderService.GetPageSize(pdfBytes, i);
            var bitmap = PdfRenderService.RenderPage(pdfBytes, i, RenderDpi);
            Pages.Add(new PageItem
            {
                Index = i, Bitmap = bitmap, WidthPt = w, HeightPt = h,
                Source = new PageSource(pdfBytes, i)
            });
        }

        RenumberPages();
        _baseStatus = $"{Path.GetFileName(path)} \u2014 {pageCount} page{(pageCount != 1 ? "s" : "")}";
        UpdateStatusText();
        PdfLoaded?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        var defaultName = PdfFilePath != null
            ? Path.GetFileNameWithoutExtension(PdfFilePath) + "_signed"
            : "document_signed";

        var file = await Storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Annotated PDF",
            SuggestedFileName = defaultName,
            DefaultExtension = ".pdf",
            FileTypeChoices = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }]
        });

        var outputPath = file?.TryGetLocalPath();
        if (outputPath == null) return;

        var pagesWithAnnotations = Pages
            .Select(p => (p.Source, (IEnumerable<Annotation>)p.Annotations));

        try
        {
            PdfSaveService.Save(outputPath, pagesWithAnnotations);
            _baseStatus = $"Saved to {Path.GetFileName(outputPath)}";
            UpdateStatusText();
        }
        catch (Exception ex)
        {
            _baseStatus = $"Save failed: {ex.Message}";
            UpdateStatusText();
        }
    }

    private bool CanSave => Pages.Count > 0;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (SelectedAnnotation == null) return;

        if (SelectedAnnotation is SvgAnnotation svg)
            svg.RenderedBitmap?.Dispose();

        foreach (var page in Pages)
        {
            if (page.Annotations.Remove(SelectedAnnotation))
                break;
        }
        SelectAnnotation(null);
    }

    private bool CanDelete => SelectedAnnotation != null;

    [RelayCommand]
    private void ToggleTextTool()
    {
        CurrentTool = CurrentTool == "Text" ? "Select" : "Text";
    }

    [RelayCommand]
    private async Task ToggleSignTool()
    {
        if (CurrentTool == "Signature")
        {
            CurrentTool = "Select";
            return;
        }

        if (SignatureSvgPath == null)
        {
            var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select SVG Signature",
                FileTypeFilter = [new FilePickerFileType("SVG") { Patterns = ["*.svg"] }]
            });

            var svgPath = files.FirstOrDefault()?.TryGetLocalPath();
            if (svgPath == null) return; // Cancelled — stay in Select mode
            SignatureSvgPath = svgPath;
        }

        CurrentTool = "Signature";
    }

    // --- Page reordering ---

    [RelayCommand]
    private void MovePageUp(PageItem page)
    {
        var idx = Pages.IndexOf(page);
        if (idx <= 0) return;
        Pages.Move(idx, idx - 1);
        RenumberPages();
    }

    [RelayCommand]
    private void MovePageDown(PageItem page)
    {
        var idx = Pages.IndexOf(page);
        if (idx < 0 || idx >= Pages.Count - 1) return;
        Pages.Move(idx, idx + 1);
        RenumberPages();
    }

    private void RenumberPages()
    {
        for (int i = 0; i < Pages.Count; i++)
        {
            Pages[i].Index = i;
            Pages[i].IsFirst = i == 0;
            Pages[i].IsLast = i == Pages.Count - 1;
            foreach (var ann in Pages[i].Annotations)
                ann.PageIndex = i;
        }
        UpdatePageCount();
    }

    private void UpdatePageCount()
    {
        var name = PdfFilePath != null ? Path.GetFileName(PdfFilePath) : "Document";
        _baseStatus = $"{name} \u2014 {Pages.Count} page{(Pages.Count != 1 ? "s" : "")}";
        UpdateStatusText();
    }

    // --- Page interleaving ---

    [RelayCommand]
    private async Task InsertPagesBefore(PageItem page)
    {
        await InsertPagesAt(Pages.IndexOf(page));
    }

    [RelayCommand]
    private async Task InsertPagesAfter(PageItem page)
    {
        await InsertPagesAt(Pages.IndexOf(page) + 1);
    }

    private async Task InsertPagesAt(int insertIndex)
    {
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Insert PDF Pages",
            FileTypeFilter = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }]
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;

        InsertPagesFromFile(path, insertIndex);
    }

    public void InsertPagesFromFile(string path, int insertIndex)
    {
        var pdfBytes = File.ReadAllBytes(path);
        var pageCount = PdfRenderService.GetPageCount(pdfBytes);

        for (int i = 0; i < pageCount; i++)
        {
            var (w, h) = PdfRenderService.GetPageSize(pdfBytes, i);
            var bitmap = PdfRenderService.RenderPage(pdfBytes, i, RenderDpi);
            var pageItem = new PageItem
            {
                Index = insertIndex + i,
                Bitmap = bitmap,
                WidthPt = w,
                HeightPt = h,
                Source = new PageSource(pdfBytes, i)
            };
            Pages.Insert(insertIndex + i, pageItem);
        }

        RenumberPages();

        if (PdfFilePath == null)
            PdfFilePath = path;
    }

    // --- PDF compression ---

    [ObservableProperty] private bool _isCompressing;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task CompressResample(string presetName)
    {
        if (!Enum.TryParse<CompressionPreset>(presetName, out var preset)) return;
        await RunCompress(preset, rasterize: false);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task CompressRasterize(string presetName)
    {
        if (!Enum.TryParse<CompressionPreset>(presetName, out var preset)) return;
        await RunCompress(preset, rasterize: true);
    }

    private async Task RunCompress(CompressionPreset preset, bool rasterize)
    {
        if (Pages.Count == 0) return;

        var suffix = rasterize ? "_rasterized" : "_compressed";
        var defaultName = PdfFilePath != null
            ? Path.GetFileNameWithoutExtension(PdfFilePath) + suffix
            : "document" + suffix;

        var file = await Storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = rasterize ? "Save Rasterized PDF" : "Save Compressed PDF",
            SuggestedFileName = defaultName,
            DefaultExtension = ".pdf",
            FileTypeChoices = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }]
        });

        var outputPath = file?.TryGetLocalPath();
        if (outputPath == null) return;

        IsCompressing = true;
        var verb = rasterize ? "Rasterizing" : "Compressing";
        _baseStatus = $"{verb}\u2026";
        UpdateStatusText();

        try
        {
            var pageSources = Pages.Select(p => p.Source).ToList();
            var progress = new Progress<int>(p =>
            {
                _baseStatus = $"{verb}\u2026 {p}%";
                UpdateStatusText();
            });

            var result = rasterize
                ? await PdfCompressService.RasterizeAsync(pageSources, outputPath, preset, progress)
                : await PdfCompressService.CompressAsync(pageSources, outputPath, preset, progress);

            var saved = result.OriginalSize - result.CompressedSize;
            if (rasterize)
            {
                _baseStatus = $"Rasterized: {FormatSize(result.OriginalSize)} \u2192 {FormatSize(result.CompressedSize)}";
            }
            else
            {
                var imgNote = result.ImagesResampled > 0
                    ? $", {result.ImagesResampled} image{(result.ImagesResampled != 1 ? "s" : "")} resampled"
                    : ", no images to resample";
                _baseStatus = saved > 0
                    ? $"Compressed: {FormatSize(result.OriginalSize)} \u2192 {FormatSize(result.CompressedSize)} ({(double)saved / result.OriginalSize * 100:F0}% smaller{imgNote})"
                    : $"Compressed: {FormatSize(result.CompressedSize)} (no reduction{imgNote})";
            }
            UpdateStatusText();
        }
        catch (Exception ex)
        {
            _baseStatus = $"{verb} failed: {ex.Message}";
            UpdateStatusText();
        }
        finally
        {
            IsCompressing = false;
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };

    // --- Canvas interaction ---

    public void OnCanvasClicked(int pageIndex, double pdfX, double pdfY)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count) return;

        if (CurrentTool == "Text")
        {
            const double w = 50, h = 18;
            var annotation = new TextAnnotation
            {
                X = pdfX - w / 2, Y = pdfY - h / 2, PageIndex = pageIndex,
                Text = "Text", FontFamily = "Helvetica",
                HeightPt = h, WidthPt = w
            };
            Pages[pageIndex].Annotations.Add(annotation);
            SelectAnnotation(annotation);
            CurrentTool = "Select";
        }
        else if (CurrentTool == "Signature" && SignatureSvgPath != null)
        {
            var (baseW, baseH) = SvgRenderService.GetSvgSize(SignatureSvgPath);
            var bitmap = SvgRenderService.RenderForDisplay(SignatureSvgPath, 1.0, RenderDpi);

            var annotation = new SvgAnnotation
            {
                X = pdfX - baseW / 2, Y = pdfY - baseH / 2, PageIndex = pageIndex,
                SvgFilePath = SignatureSvgPath,
                Scale = 1.0,
                OriginalWidthPt = baseW, OriginalHeightPt = baseH,
                WidthPt = baseW, HeightPt = baseH,
                RenderedBitmap = bitmap
            };
            Pages[pageIndex].Annotations.Add(annotation);
            SelectAnnotation(annotation);
            CurrentTool = "Select";
        }
        else
        {
            SelectAnnotation(null);
        }
    }

    public void SelectAnnotation(Annotation? annotation)
    {
        if (SelectedAnnotation != null)
            SelectedAnnotation.IsSelected = false;

        SelectedAnnotation = annotation;

        if (annotation != null)
            annotation.IsSelected = true;
    }

}
