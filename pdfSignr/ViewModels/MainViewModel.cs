using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using pdfSignr.Models;
using pdfSignr.Services;

namespace pdfSignr.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IFileDialogService _fileDialogs;

    [ObservableProperty] private string? _pdfFilePath;
    [ObservableProperty] private ObservableCollection<PageItem> _pages = new();
    [ObservableProperty] private Annotation? _selectedAnnotation;
    [ObservableProperty] private ToolMode _currentTool = ToolMode.Select;
    private string _baseStatus = "Open a PDF to get started";
    private string BaseStatus
    {
        get => _baseStatus;
        set { _baseStatus = value; UpdateStatusText(); }
    }
    [ObservableProperty] private string _statusText = "Open a PDF to get started";
    [ObservableProperty] private string? _signatureSvgPath;
    [ObservableProperty] private bool _isDraggingFile;
    [ObservableProperty] private double _buttonScale = 1.0;
    [ObservableProperty] private double _insertGapHeight = 28;
    [ObservableProperty] private bool _isGridMode;
    [ObservableProperty] private bool _isDraggingPage;
    [ObservableProperty] private Thickness _selectionBorderThickness = new(3);

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
    public event Action<PageItem>? PageRotated;

    public UndoRedoService UndoRedo { get; } = new();

    public MainViewModel(IFileDialogService fileDialogs)
    {
        _fileDialogs = fileDialogs;
        Pages.CollectionChanged += OnPagesCollectionChanged;
        UndoRedo.PropertyChanged += (_, _) =>
        {
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        };
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
        SavePageRangeCommand.NotifyCanExecuteChanged();
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
        var selInfo = SelectedPageCount > 1 ? $"  \u2502  {SelectedPageCount} pages selected" : "";
        StatusText = $"{_baseStatus}{info}{selInfo}  \u2502  {ZoomPercent}%";
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

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => UndoRedo.Undo();
    private bool CanUndo => UndoRedo.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => UndoRedo.Redo();
    private bool CanRedo => UndoRedo.CanRedo;

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

            var newPages = await Task.Run(() => LoadPdfCore(path));

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

    private static PageItem[] CreatePageItems(byte[] pdfBytes, int startIndex = 0)
    {
        var sizes = PdfRenderService.GetAllPageSizes(pdfBytes);
        int count = sizes.Count;

        var items = new PageItem[count];
        for (int i = 0; i < count; i++)
        {
            var (w, h) = sizes[i];
            items[i] = new PageItem
            {
                Index = startIndex + i,
                Bitmap = null, OriginalWidthPt = w, OriginalHeightPt = h,
                Source = new PageSource(pdfBytes, i)
            };
        }
        return items;
    }

    private static ObservableCollection<PageItem> LoadPdfCore(string path)
    {
        var items = CreatePageItems(File.ReadAllBytes(path));
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

        var outputPath = await _fileDialogs.PickSaveFileAsync("Save Annotated PDF", defaultName, ".pdf", ["*.pdf"]);
        if (outputPath == null) return;

        var pagesWithAnnotations = Pages
            .Select(p => (p.Source, p.RotationDegrees, p.OriginalWidthPt, p.OriginalHeightPt,
                          (IEnumerable<Annotation>)p.Annotations));

        try
        {
            PdfSaveService.Save(outputPath, pagesWithAnnotations);
            BaseStatus = $"Saved to {Path.GetFileName(outputPath)}";
        }
        catch (Exception ex)
        {
            BaseStatus = $"Save failed: {ex.Message}";
            await _fileDialogs.ShowErrorAsync("Save Failed", ex.Message);
        }
    }

    private bool CanSave => Pages.Count > 0;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (SelectedAnnotation == null) return;
        var ann = SelectedAnnotation;

        PageItem? ownerPage = null;
        int annIndex = -1;
        foreach (var page in Pages)
        {
            int idx = page.Annotations.IndexOf(ann);
            if (idx >= 0) { ownerPage = page; annIndex = idx; break; }
        }
        if (ownerPage == null) return;

        ownerPage.Annotations.RemoveAt(annIndex);
        SelectAnnotation(null);

        UndoRedo.Push(new UndoEntry(
            "Delete annotation",
            Undo: () => { ownerPage.Annotations.Insert(annIndex, ann); SelectAnnotation(ann); },
            Redo: () => { ownerPage.Annotations.Remove(ann); SelectAnnotation(null); }
        ));
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

    // --- Page removal / reordering ---

    public event Action? PageStructureChanged;

    [RelayCommand]
    private void MovePageUp(PageItem page)
    {
        if (page.IsSelected && SelectedPageCount > 1)
        {
            MoveSelectedPagesUp();
            return;
        }

        var idx = Pages.IndexOf(page);
        if (idx <= 0) return;
        Pages.Move(idx, idx - 1);
        RenumberPages();

        UndoRedo.Push(new UndoEntry(
            "Move page up",
            Undo: () => { Pages.Move(idx - 1, idx); RenumberPages(); },
            Redo: () => { Pages.Move(idx, idx - 1); RenumberPages(); }
        ));
    }

    [RelayCommand]
    private void MovePageDown(PageItem page)
    {
        if (page.IsSelected && SelectedPageCount > 1)
        {
            MoveSelectedPagesDown();
            return;
        }

        var idx = Pages.IndexOf(page);
        if (idx < 0 || idx >= Pages.Count - 1) return;
        Pages.Move(idx, idx + 1);
        RenumberPages();

        UndoRedo.Push(new UndoEntry(
            "Move page down",
            Undo: () => { Pages.Move(idx + 1, idx); RenumberPages(); },
            Redo: () => { Pages.Move(idx, idx + 1); RenumberPages(); }
        ));
    }

    private void MoveSelectedPagesUp()
    {
        var orderBefore = Pages.ToList();
        var selectedIndices = Pages
            .Select((p, i) => (p, i))
            .Where(x => x.p.IsSelected)
            .Select(x => x.i)
            .OrderBy(i => i)
            .ToList();

        int barrier = 0;
        bool moved = false;
        foreach (var idx in selectedIndices)
        {
            if (idx > barrier)
            {
                Pages.Move(idx, idx - 1);
                moved = true;
                // The non-selected page that was at idx-1 is now at idx
            }
            else
            {
                barrier = idx + 1;
            }
        }

        if (!moved) return;
        RenumberPages();

        var orderAfter = Pages.ToList();
        UndoRedo.Push(new UndoEntry(
            "Move selected pages up",
            Undo: () => ReorderPages(orderBefore),
            Redo: () => ReorderPages(orderAfter)
        ));
    }

    private void MoveSelectedPagesDown()
    {
        var orderBefore = Pages.ToList();
        var selectedIndices = Pages
            .Select((p, i) => (p, i))
            .Where(x => x.p.IsSelected)
            .Select(x => x.i)
            .OrderByDescending(i => i)
            .ToList();

        int barrier = Pages.Count - 1;
        bool moved = false;
        foreach (var idx in selectedIndices)
        {
            if (idx < barrier)
            {
                Pages.Move(idx, idx + 1);
                moved = true;
            }
            else
            {
                barrier = idx - 1;
            }
        }

        if (!moved) return;
        RenumberPages();

        var orderAfter = Pages.ToList();
        UndoRedo.Push(new UndoEntry(
            "Move selected pages down",
            Undo: () => ReorderPages(orderBefore),
            Redo: () => ReorderPages(orderAfter)
        ));
    }

    private void ReorderPages(IReadOnlyList<PageItem> order)
    {
        Pages.Clear();
        foreach (var p in order) Pages.Add(p);
        RenumberPages();
        PageStructureChanged?.Invoke();
    }

    public void MovePageByDrag(int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex || fromIndex < 0 || toIndex < 0) return;
        Pages.Move(fromIndex, toIndex);
        RenumberPages();
        PageStructureChanged?.Invoke();

        UndoRedo.Push(new UndoEntry(
            "Reorder page",
            Undo: () => { Pages.Move(toIndex, fromIndex); RenumberPages(); PageStructureChanged?.Invoke(); },
            Redo: () => { Pages.Move(fromIndex, toIndex); RenumberPages(); PageStructureChanged?.Invoke(); }
        ));
    }

    public void MovePagesByDrag(IReadOnlyList<PageItem> pages, int targetIndex)
    {
        if (pages.Count == 0 || targetIndex < 0) return;

        var orderBefore = Pages.ToList();

        // Get original indices sorted descending for safe removal
        var indices = pages.Select(p => Pages.IndexOf(p)).Where(i => i >= 0).OrderByDescending(i => i).ToList();
        int countBefore = indices.Count(i => i < targetIndex);

        foreach (var idx in indices)
            Pages.RemoveAt(idx);

        // Adjust insert position: for each removed page that was before target, target shifts down by 1
        int insertAt = Math.Min(targetIndex - countBefore, Pages.Count);

        // Insert in original relative order (ascending)
        var ordered = pages.Where(p => !Pages.Contains(p)).ToList();
        for (int i = 0; i < ordered.Count; i++)
            Pages.Insert(insertAt + i, ordered[i]);

        RenumberPages();
        PageStructureChanged?.Invoke();

        var orderAfter = Pages.ToList();
        UndoRedo.Push(new UndoEntry(
            $"Reorder {pages.Count} pages",
            Undo: () => ReorderPages(orderBefore),
            Redo: () => ReorderPages(orderAfter)
        ));
    }

    // --- Page rotation ---

    [RelayCommand]
    private void RotatePageCw(PageItem page)
    {
        if (page.IsSelected && SelectedPageCount > 1)
            RotateSelectedPages(90);
        else
            RotatePage(page, 90);
    }

    [RelayCommand]
    private void RotatePageCcw(PageItem page)
    {
        if (page.IsSelected && SelectedPageCount > 1)
            RotateSelectedPages(270);
        else
            RotatePage(page, 270);
    }

    private void RotatePage(PageItem page, int degrees)
    {
        int oldRotation = page.RotationDegrees;
        double oldW = page.WidthPt;
        double oldH = page.HeightPt;

        // Snapshot annotation positions before rotation for undo
        var annSnaps = page.Annotations.Select(a => (Ann: a, a.X, a.Y)).ToList();

        page.RotateAnnotations(degrees, oldW, oldH);
        page.RotationDegrees = (page.RotationDegrees + degrees) % 360;
        int newRotation = page.RotationDegrees;

        // Snapshot after
        var annSnapsAfter = page.Annotations.Select(a => (Ann: a, a.X, a.Y)).ToList();

        PageRotated?.Invoke(page);

        UndoRedo.Push(new UndoEntry(
            "Rotate page",
            Undo: () =>
            {
                page.RotationDegrees = oldRotation;
                foreach (var (ann, x, y) in annSnaps) { ann.X = x; ann.Y = y; }
                PageRotated?.Invoke(page);
            },
            Redo: () =>
            {
                page.RotationDegrees = newRotation;
                foreach (var (ann, x, y) in annSnapsAfter) { ann.X = x; ann.Y = y; }
                PageRotated?.Invoke(page);
            }
        ));
    }

    private void RotateSelectedPages(int degrees)
    {
        var selected = SelectedPages.ToList();
        var snapshots = selected.Select(p => new
        {
            Page = p,
            OldRotation = p.RotationDegrees,
            OldW = p.WidthPt,
            OldH = p.HeightPt,
            AnnsBefore = p.Annotations.Select(a => (Ann: a, a.X, a.Y)).ToList()
        }).ToList();

        foreach (var snap in snapshots)
        {
            snap.Page.RotateAnnotations(degrees, snap.OldW, snap.OldH);
            snap.Page.RotationDegrees = (snap.Page.RotationDegrees + degrees) % 360;
        }

        var snapshotsAfter = snapshots.Select(s => new
        {
            s.Page,
            NewRotation = s.Page.RotationDegrees,
            AnnsAfter = s.Page.Annotations.Select(a => (Ann: a, a.X, a.Y)).ToList()
        }).ToList();

        foreach (var snap in snapshots)
            PageRotated?.Invoke(snap.Page);

        UndoRedo.Push(new UndoEntry(
            $"Rotate {selected.Count} pages",
            Undo: () =>
            {
                foreach (var snap in snapshots)
                {
                    snap.Page.RotationDegrees = snap.OldRotation;
                    foreach (var (ann, x, y) in snap.AnnsBefore) { ann.X = x; ann.Y = y; }
                    PageRotated?.Invoke(snap.Page);
                }
            },
            Redo: () =>
            {
                foreach (var snap in snapshotsAfter)
                {
                    snap.Page.RotationDegrees = snap.NewRotation;
                    foreach (var (ann, x, y) in snap.AnnsAfter) { ann.X = x; ann.Y = y; }
                    PageRotated?.Invoke(snap.Page);
                }
            }
        ));
    }

    public void RenumberPages()
    {
        for (int i = 0; i < Pages.Count; i++)
        {
            Pages[i].Index = i;
            Pages[i].DisplayNumber = i + 1;
            Pages[i].IsFirst = i == 0;
            foreach (var ann in Pages[i].Annotations)
                ann.PageIndex = i;
        }
        UpdatePageCount();
    }

    private void UpdatePageCount()
    {
        var name = PdfFilePath != null ? Path.GetFileName(PdfFilePath) : "Document";
        BaseStatus = $"{name} \u2014 {Pages.Count} page{(Pages.Count != 1 ? "s" : "")}";
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
                BaseStatus = $"File not found: {Path.GetFileName(path)}";
                return;
            }

            // All heavy work on the thread pool
            var newPages = await Task.Run(() =>
                CreatePageItems(File.ReadAllBytes(path), insertIndex));

            for (int i = 0; i < newPages.Length; i++)
                Pages.Insert(insertIndex + i, newPages[i]);

            RenumberPages();

            if (PdfFilePath == null)
                PdfFilePath = path;

            var insertedPages = newPages.ToList();
            int count = insertedPages.Count;
            int insertAt = insertIndex;
            UndoRedo.Push(new UndoEntry(
                "Insert pages",
                Undo: () =>
                {
                    for (int i = count - 1; i >= 0; i--)
                        Pages.RemoveAt(insertAt + i);
                    if (Pages.Count == 0) { PdfFilePath = null; BaseStatus = "Open a PDF to get started"; }
                    else RenumberPages();
                },
                Redo: () =>
                {
                    for (int i = 0; i < count; i++)
                        Pages.Insert(insertAt + i, insertedPages[i]);
                    RenumberPages();
                }
            ));
        }
        catch (Exception ex)
        {
            BaseStatus = $"Failed to insert: {ex.Message}";
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
        BaseStatus = $"{verb}\u2026";

        try
        {
            var pageSources = Pages.Select(p => (p.Source, p.RotationDegrees)).ToList();
            var progress = new Progress<int>(p =>
            {
                BaseStatus = $"{verb}\u2026 {p}%";
            });

            var result = rasterize
                ? await PdfCompressService.RasterizeAsync(pageSources, outputPath, preset, progress)
                : await PdfCompressService.CompressAsync(pageSources, outputPath, preset, progress);

            var saved = result.OriginalSize - result.CompressedSize;
            if (rasterize)
            {
                BaseStatus = $"Rasterized: {FormatSize(result.OriginalSize)} \u2192 {FormatSize(result.CompressedSize)}";
            }
            else
            {
                var imgNote = result.ImagesResampled > 0
                    ? $", {result.ImagesResampled} image{(result.ImagesResampled != 1 ? "s" : "")} resampled"
                    : ", no images to resample";
                BaseStatus = saved > 0
                    ? $"Compressed: {FormatSize(result.OriginalSize)} \u2192 {FormatSize(result.CompressedSize)} ({(double)saved / result.OriginalSize * 100:F0}% smaller{imgNote})"
                    : $"Compressed: {FormatSize(result.CompressedSize)} (no reduction{imgNote})";
            }
        }
        catch (Exception ex)
        {
            BaseStatus = $"{verb} failed: {ex.Message}";
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

        var page = Pages[pageIndex];

        if (CurrentTool == ToolMode.Text)
        {
            const double w = 50, h = 18;
            var annotation = new TextAnnotation
            {
                X = Math.Clamp(pdfX - w / 2, 0, Math.Max(0, page.WidthPt - w)),
                Y = Math.Clamp(pdfY - h / 2, 0, Math.Max(0, page.HeightPt - h)),
                PageIndex = pageIndex,
                Text = "Text", FontFamily = FontResolver.PdfFontNames[0],
                HeightPt = h, WidthPt = w
            };
            page.Annotations.Add(annotation);
            SelectAnnotation(annotation);
            CurrentTool = ToolMode.Select;

            UndoRedo.Push(new UndoEntry(
                "Add text",
                Undo: () => { page.Annotations.Remove(annotation); SelectAnnotation(null); },
                Redo: () => { page.Annotations.Add(annotation); SelectAnnotation(annotation); }
            ));
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
                (baseW, baseH, bitmap) = SvgRenderService.GetSizeAndRenderForDisplay(SignatureSvgPath, 1.0, PdfConstants.RenderDpi);
            }

            var annotation = new SvgAnnotation
            {
                X = Math.Clamp(pdfX - baseW / 2, 0, Math.Max(0, page.WidthPt - baseW)),
                Y = Math.Clamp(pdfY - baseH / 2, 0, Math.Max(0, page.HeightPt - baseH)),
                PageIndex = pageIndex,
                SvgFilePath = SignatureSvgPath,
                IsRaster = isRaster,
                Scale = 1.0,
                OriginalWidthPt = baseW, OriginalHeightPt = baseH,
                WidthPt = baseW, HeightPt = baseH,
                RenderedBitmap = bitmap,
                RenderedDpi = PdfConstants.RenderDpi
            };
            page.Annotations.Add(annotation);
            SelectAnnotation(annotation);
            CurrentTool = ToolMode.Select;

            UndoRedo.Push(new UndoEntry(
                "Add signature",
                Undo: () => { page.Annotations.Remove(annotation); SelectAnnotation(null); },
                Redo: () => { page.Annotations.Add(annotation); SelectAnnotation(annotation); }
            ));
        }
        else
        {
            SelectAnnotation(null);
        }
    }

    // --- Save page range ---

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

        var outputPath = await _fileDialogs.PickSaveFileAsync(
            $"Save pages {from}\u2013{to}", baseName + suffix, ".pdf", ["*.pdf"]);
        if (outputPath == null) return;

        try
        {
            var pagesInRange = Pages.Skip(from - 1).Take(to - from + 1)
                .Select(p => (p.Source, p.RotationDegrees, p.OriginalWidthPt, p.OriginalHeightPt,
                              (IEnumerable<Annotation>)p.Annotations));
            PdfSaveService.Save(outputPath, pagesInRange);
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

    public void SelectAnnotation(Annotation? annotation)
    {
        if (SelectedAnnotation != null)
            SelectedAnnotation.IsSelected = false;

        SelectedAnnotation = annotation;

        if (annotation != null)
            annotation.IsSelected = true;
    }

    // --- Page selection ---

    [ObservableProperty] private int _selectedPageCount;
    private int _lastClickedPageIndex = -1;

    public IReadOnlyList<PageItem> SelectedPages => Pages.Where(p => p.IsSelected).ToList();
    public bool HasSelectedPages => SelectedPageCount > 0;

    public void SelectPage(PageItem page, bool ctrl, bool shift)
    {
        if (shift && _lastClickedPageIndex >= 0)
        {
            ClearPageSelection();
            int from = Math.Min(_lastClickedPageIndex, page.Index);
            int to = Math.Max(_lastClickedPageIndex, page.Index);
            for (int i = from; i <= to && i < Pages.Count; i++)
                Pages[i].IsSelected = true;
        }
        else if (ctrl)
        {
            page.IsSelected = !page.IsSelected;
            _lastClickedPageIndex = page.Index;
        }
        else
        {
            ClearPageSelection();
            page.IsSelected = true;
            _lastClickedPageIndex = page.Index;
        }
        UpdateSelectionState();
    }

    public void SelectAllPages()
    {
        foreach (var p in Pages) p.IsSelected = true;
        UpdateSelectionState();
    }

    public void ClearPageSelection()
    {
        foreach (var p in Pages) p.IsSelected = false;
        UpdateSelectionState();
    }

    public void UpdateSelectionState()
    {
        SelectedPageCount = Pages.Count(p => p.IsSelected);
        OnPropertyChanged(nameof(HasSelectedPages));
        SaveSelectedPagesCommand.NotifyCanExecuteChanged();
        DeleteSelectedPagesCommand.NotifyCanExecuteChanged();
        ExtractSelectedPagesCommand.NotifyCanExecuteChanged();
        UpdateStatusText();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedPages))]
    private async Task SaveSelectedPages()
    {
        var selected = SelectedPages;
        var defaultName = PdfFilePath != null
            ? Path.GetFileNameWithoutExtension(PdfFilePath) + "_selected"
            : "document_selected";

        var outputPath = await _fileDialogs.PickSaveFileAsync("Save Selected Pages", defaultName, ".pdf", ["*.pdf"]);
        if (outputPath == null) return;

        var pagesWithAnnotations = selected
            .Select(p => (p.Source, p.RotationDegrees, p.OriginalWidthPt, p.OriginalHeightPt,
                          (IEnumerable<Annotation>)p.Annotations));

        try
        {
            PdfSaveService.Save(outputPath, pagesWithAnnotations);
            BaseStatus = $"Saved {selected.Count} page{(selected.Count != 1 ? "s" : "")} to {Path.GetFileName(outputPath)}";
        }
        catch (Exception ex)
        {
            BaseStatus = $"Save failed: {ex.Message}";
            await _fileDialogs.ShowErrorAsync("Save Failed", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedPages))]
    private void DeleteSelectedPages()
    {
        var toRemove = SelectedPages.ToList();
        var snapshots = toRemove.Select(p => (Page: p, Index: Pages.IndexOf(p))).OrderByDescending(x => x.Index).ToList();

        if (SelectedAnnotation != null && toRemove.Any(p => p.Annotations.Contains(SelectedAnnotation)))
            SelectAnnotation(null);

        foreach (var (page, _) in snapshots)
            Pages.Remove(page);

        if (Pages.Count == 0) { PdfFilePath = null; BaseStatus = "Open a PDF to get started"; }
        else RenumberPages();
        PageStructureChanged?.Invoke();

        UndoRedo.Push(new UndoEntry(
            "Delete selected pages",
            Undo: () =>
            {
                foreach (var (page, idx) in snapshots.OrderBy(x => x.Index))
                    Pages.Insert(idx, page);
                RenumberPages();
                PageStructureChanged?.Invoke();
            },
            Redo: () =>
            {
                foreach (var (page, _) in snapshots)
                    Pages.Remove(page);
                if (Pages.Count == 0) { PdfFilePath = null; BaseStatus = "Open a PDF to get started"; }
                else RenumberPages();
                PageStructureChanged?.Invoke();
            }
        ));
    }

    [RelayCommand(CanExecute = nameof(HasSelectedPages))]
    private async Task ExtractSelectedPages()
    {
        var selected = SelectedPages;
        var defaultName = PdfFilePath != null
            ? Path.GetFileNameWithoutExtension(PdfFilePath) + "_extract"
            : "extract";

        var outputPath = await _fileDialogs.PickSaveFileAsync("Extract Selected Pages", defaultName, ".pdf", ["*.pdf"]);
        if (outputPath == null) return;

        var pagesWithAnnotations = selected
            .Select(p => (p.Source, p.RotationDegrees, p.OriginalWidthPt, p.OriginalHeightPt,
                          (IEnumerable<Annotation>)p.Annotations));

        try
        {
            PdfSaveService.Save(outputPath, pagesWithAnnotations);
            BaseStatus = $"Extracted {selected.Count} page{(selected.Count != 1 ? "s" : "")} to {Path.GetFileName(outputPath)}";
        }
        catch (Exception ex)
        {
            BaseStatus = $"Extract failed: {ex.Message}";
            await _fileDialogs.ShowErrorAsync("Extract Failed", ex.Message);
        }
    }

}
