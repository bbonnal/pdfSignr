using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using pdfSignr.Models;
using pdfSignr.Services;

namespace pdfSignr.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public const int RenderDpi = PdfConstants.RenderDpi;
    public const double DpiScale = PdfConstants.DpiScale;

    private readonly IFileDialogService _fileDialogs;

    [ObservableProperty] private string? _pdfFilePath;
    [ObservableProperty] private ObservableCollection<PageItem> _pages = new();
    [ObservableProperty] private Annotation? _selectedAnnotation;
    [ObservableProperty] private ToolMode _currentTool = ToolMode.Select;
    private string _baseStatus = "Open a PDF to get started";
    [ObservableProperty] private string _statusText = "Open a PDF to get started";
    [ObservableProperty] private string? _signatureSvgPath;
    [ObservableProperty] private bool _isDraggingFile;
    [ObservableProperty] private double _buttonScale = 1.0;
    [ObservableProperty] private double _insertGapHeight = 28;
    [ObservableProperty] private bool _isGridMode;
    [ObservableProperty] private bool _isDraggingPage;

    public bool IsNotDraggingFile => !IsDraggingFile;
    public bool IsNotDragging => !IsDraggingFile && !IsDraggingPage;
    public bool HasNoPages => Pages.Count == 0;
    public bool IsNotGridMode => !IsGridMode;

    partial void OnIsDraggingFileChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotDraggingFile));
        OnPropertyChanged(nameof(IsNotDragging));
    }

    partial void OnIsDraggingPageChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotDragging));
    }

    partial void OnIsGridModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotGridMode));
    }

    public void SetDropTarget(int insertIndex)
    {
        ClearDropTargets();
        if (insertIndex == 0 && Pages.Count > 0)
            Pages[0].IsDropTargetBefore = true;
        else if (insertIndex > 0 && insertIndex <= Pages.Count)
            Pages[insertIndex - 1].IsDropTargetAfter = true;
    }

    public void ClearDropTargets()
    {
        foreach (var page in Pages)
        {
            page.IsDropTargetAfter = false;
            page.IsDropTargetBefore = false;
        }
    }

    public static string[] AvailableFonts => FontResolver.PdfFontNames;

    // Set by MainWindow when zoom changes
    public int ZoomPercent { get; set; } = 100;

    // Computed tool-active properties for Ribbon ToggleButton binding
    public bool IsTextToolActive => CurrentTool == ToolMode.Text;
    public bool IsSignToolActive => CurrentTool == ToolMode.Signature;
    public string TextToolTip => IsTextToolActive ? "Click on page to place text" : "Add text";
    public string SignToolTip => IsSignToolActive ? "Click on page to place signature" : "Add signature";
    private static readonly Cursor DragMoveCursor = new(StandardCursorType.DragMove);
    public Cursor? PlacementCursor => CurrentTool is ToolMode.Text or ToolMode.Signature
        ? DragMoveCursor : null;

    public event Action? PdfLoaded;

    public MainViewModel(IFileDialogService fileDialogs)
    {
        _fileDialogs = fileDialogs;
        Pages.CollectionChanged += OnPagesCollectionChanged;
    }

    partial void OnPagesChanged(ObservableCollection<PageItem>? oldValue, ObservableCollection<PageItem> newValue)
    {
        if (oldValue != null) oldValue.CollectionChanged -= OnPagesCollectionChanged;
        newValue.CollectionChanged += OnPagesCollectionChanged;
        NotifyCollectionDependents();
    }

    private void OnPagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => NotifyCollectionDependents();

    private void NotifyCollectionDependents()
    {
        SaveCommand.NotifyCanExecuteChanged();
        CompressResampleCommand.NotifyCanExecuteChanged();
        CompressRasterizeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasNoPages));
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

    partial void OnCurrentToolChanged(ToolMode value)
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
                _baseStatus = $"File not found: {Path.GetFileName(path)}";
                UpdateStatusText();
                return;
            }

            var fileName = Path.GetFileName(path);
            _baseStatus = $"Opening {fileName}\u2026";
            UpdateStatusText();

            // All heavy work on the thread pool: read file, get sizes, render every page
            var progress = new Progress<int>(pct =>
            {
                _baseStatus = $"Opening {fileName}\u2026 {pct}%";
                UpdateStatusText();
            });

            var newPages = await Task.Run(() => LoadPdfCore(path, progress));

            // Dispose old pages, then swap the entire collection in one shot
            foreach (var page in Pages)
                page.DisposeResources();

            PdfFilePath = path;
            SelectAnnotation(null);
            Pages = newPages;

            _baseStatus = $"{fileName} \u2014 {newPages.Count} page{(newPages.Count != 1 ? "s" : "")}";
            UpdateStatusText();
            PdfLoaded?.Invoke();
        }
        catch (Exception ex)
        {
            _baseStatus = $"Failed to open: {ex.Message}";
            UpdateStatusText();
            _ = _fileDialogs.ShowErrorAsync("Failed to Open", ex.Message);
        }
    }

    private static ObservableCollection<PageItem> LoadPdfCore(string path, IProgress<int> progress)
    {
        var pdfBytes = File.ReadAllBytes(path);
        var sizes = PdfRenderService.GetAllPageSizes(pdfBytes);
        int count = sizes.Count;

        // Pre-allocate items with metadata (fast)
        var items = new PageItem[count];
        for (int i = 0; i < count; i++)
        {
            var (w, h) = sizes[i];
            items[i] = new PageItem
            {
                Index = i, DisplayNumber = i + 1,
                IsFirst = i == 0, IsLast = i == count - 1,
                Bitmap = null, WidthPt = w, HeightPt = h,
                Source = new PageSource(pdfBytes, i)
            };
        }

        // Render all pages in parallel across available cores
        int done = 0;
        Parallel.For(0, count, i =>
        {
            items[i].Bitmap = PdfRenderService.RenderPage(pdfBytes, i, RenderDpi);
            var pct = Interlocked.Increment(ref done) * 100 / count;
            progress.Report(pct);
        });

        var collection = new ObservableCollection<PageItem>(items);
        return collection;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        var defaultName = PdfFilePath != null
            ? Path.GetFileNameWithoutExtension(PdfFilePath) + "_signed"
            : "document_signed";

        var outputPath = await _fileDialogs.PickSaveFileAsync("Save Annotated PDF", defaultName, ".pdf", ["*.pdf"]);
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
            await _fileDialogs.ShowErrorAsync("Save Failed", ex.Message);
        }
    }

    private bool CanSave => Pages.Count > 0;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (SelectedAnnotation == null) return;

        SelectedAnnotation.Dispose();

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
        CurrentTool = CurrentTool == ToolMode.Text ? ToolMode.Select : ToolMode.Text;
    }

    [RelayCommand]
    private async Task ToggleSignTool()
    {
        if (CurrentTool == ToolMode.Signature)
        {
            CurrentTool = ToolMode.Select;
            return;
        }

        if (SignatureSvgPath == null)
        {
            var path = await _fileDialogs.PickOpenFileAsync("Select Signature", ["*.svg", "*.png", "*.jpg", "*.jpeg"]);
            if (path == null) return; // Cancelled — stay in Select mode
            SignatureSvgPath = path;
        }

        CurrentTool = ToolMode.Signature;
    }

    // --- Save single page ---

    [RelayCommand]
    private async Task SaveSinglePage(PageItem page)
    {
        var defaultName = PdfFilePath != null
            ? Path.GetFileNameWithoutExtension(PdfFilePath) + $"_page{page.Index + 1}"
            : $"page{page.Index + 1}";

        var outputPath = await _fileDialogs.PickSaveFileAsync("Save Single Page", defaultName, ".pdf", ["*.pdf"]);
        if (outputPath == null) return;

        try
        {
            PdfSaveService.SaveSinglePage(outputPath, page.Source, page.Annotations);
            _baseStatus = $"Page {page.Index + 1} saved to {Path.GetFileName(outputPath)}";
            UpdateStatusText();
        }
        catch (Exception ex)
        {
            _baseStatus = $"Save failed: {ex.Message}";
            UpdateStatusText();
            await _fileDialogs.ShowErrorAsync("Save Failed", ex.Message);
        }
    }

    // --- Page removal ---

    public event Action? PageStructureChanged;

    [RelayCommand]
    private async Task RemovePage(PageItem page)
    {
        var idx = Pages.IndexOf(page);
        if (idx < 0) return;

        if (!await _fileDialogs.ConfirmAsync("Remove Page", $"Remove page {page.DisplayNumber}? This cannot be undone."))
            return;

        page.DisposeResources();

        if (SelectedAnnotation != null && page.Annotations.Contains(SelectedAnnotation))
            SelectAnnotation(null);

        Pages.RemoveAt(idx);

        if (Pages.Count == 0)
        {
            PdfFilePath = null;
            _baseStatus = "Open a PDF to get started";
            UpdateStatusText();
        }
        else
        {
            RenumberPages();
        }

        PageStructureChanged?.Invoke();
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

    public void RenumberPages()
    {
        for (int i = 0; i < Pages.Count; i++)
        {
            Pages[i].Index = i;
            Pages[i].DisplayNumber = i + 1;
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
        var path = await _fileDialogs.PickOpenFileAsync("Insert PDF Pages", ["*.pdf"]);
        if (path == null) return;

        await InsertPagesFromFileAsync(path, insertIndex);
    }

    public async Task InsertPagesFromFileAsync(string path, int insertIndex)
    {
        try
        {
            if (!File.Exists(path))
            {
                _baseStatus = $"File not found: {Path.GetFileName(path)}";
                UpdateStatusText();
                return;
            }

            // All heavy work on the thread pool
            var newPages = await Task.Run(() =>
            {
                var pdfBytes = File.ReadAllBytes(path);
                var sizes = PdfRenderService.GetAllPageSizes(pdfBytes);
                int count = sizes.Count;

                var items = new PageItem[count];
                for (int i = 0; i < count; i++)
                {
                    var (w, h) = sizes[i];
                    items[i] = new PageItem
                    {
                        Index = insertIndex + i,
                        Bitmap = null,
                        WidthPt = w, HeightPt = h,
                        Source = new PageSource(pdfBytes, i)
                    };
                }

                Parallel.For(0, count, i =>
                {
                    items[i].Bitmap = PdfRenderService.RenderPage(pdfBytes, i, RenderDpi);
                });

                return items;
            });

            for (int i = 0; i < newPages.Length; i++)
                Pages.Insert(insertIndex + i, newPages[i]);

            RenumberPages();

            if (PdfFilePath == null)
                PdfFilePath = path;
        }
        catch (Exception ex)
        {
            _baseStatus = $"Failed to insert: {ex.Message}";
            UpdateStatusText();
            _ = _fileDialogs.ShowErrorAsync("Failed to Insert", ex.Message);
        }
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

        var title = rasterize ? "Save Rasterized PDF" : "Save Compressed PDF";
        var outputPath = await _fileDialogs.PickSaveFileAsync(title, defaultName, ".pdf", ["*.pdf"]);
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
            await _fileDialogs.ShowErrorAsync($"{verb} Failed", ex.Message);
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

        if (CurrentTool == ToolMode.Text)
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
            CurrentTool = ToolMode.Select;
        }
        else if (CurrentTool == ToolMode.Signature && SignatureSvgPath != null)
        {
            bool isRaster = !SignatureSvgPath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);

            double baseW, baseH;
            Avalonia.Media.Imaging.Bitmap bitmap;

            if (isRaster)
            {
                (baseW, baseH) = SvgRenderService.GetImageSize(SignatureSvgPath);
                bitmap = new Avalonia.Media.Imaging.Bitmap(SignatureSvgPath);
            }
            else
            {
                (baseW, baseH, bitmap) = SvgRenderService.GetSizeAndRenderForDisplay(SignatureSvgPath, 1.0, RenderDpi);
            }

            var annotation = new SvgAnnotation
            {
                X = pdfX - baseW / 2, Y = pdfY - baseH / 2, PageIndex = pageIndex,
                SvgFilePath = SignatureSvgPath,
                IsRaster = isRaster,
                Scale = 1.0,
                OriginalWidthPt = baseW, OriginalHeightPt = baseH,
                WidthPt = baseW, HeightPt = baseH,
                RenderedBitmap = bitmap,
                RenderedDpi = RenderDpi
            };
            Pages[pageIndex].Annotations.Add(annotation);
            SelectAnnotation(annotation);
            CurrentTool = ToolMode.Select;
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
