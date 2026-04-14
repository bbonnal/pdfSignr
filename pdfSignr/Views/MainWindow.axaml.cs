using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Iconr;
using pdfSignr.Models;
using pdfSignr.Services;
using pdfSignr.ViewModels;

namespace pdfSignr.Views;

public partial class MainWindow : Window
{
    private const double ZoomStep = 0.1;
    private const double MinZoom = 0.1;
    private const double MaxZoom = 5.0;

    private MainViewModel ViewModel => (MainViewModel)DataContext!;
    private TextAnnotation? _editingText;
    private double _zoom = 1.0;
    private double _screenScaling = 1.0;
    private int _targetDpi = MainViewModel.RenderDpi;
    private readonly Dictionary<int, int> _pageDpi = new(); // page index → DPI last rendered at
    private DispatcherTimer? _rerenderTimer;
    private DispatcherTimer? _scrollTimer;
    private CancellationTokenSource? _rerenderCts;

    public MainWindow()
    {
        InitializeComponent();

        AddHandler(PageCanvas.CanvasClickedEvent, OnCanvasClicked);
        AddHandler(PageCanvas.AnnotationSelectedEvent, OnAnnotationSelected);
        AddHandler(PageCanvas.DeleteRequestedEvent, OnDeleteRequested);
        // Tunnel so we get the event before ScrollViewer consumes it
        PdfScrollViewer.AddHandler(PointerWheelChangedEvent, OnScrollWheel, RoutingStrategies.Tunnel);

        // Re-render newly visible pages after scrolling stops
        PdfScrollViewer.ScrollChanged += OnScrollChanged;

        // Drag-and-drop for PDF files — accept drops anywhere in the window
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Size window to full vertical extent of the current screen
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen != null)
        {
            var scaling = screen.Scaling;
            _screenScaling = scaling;
            var workArea = screen.WorkingArea;

            // Account for window frame decorations (title bar + borders)
            double frameOverhead = FrameSize is { } frame
                ? frame.Height - ClientSize.Height
                : 32; // safe fallback for typical title bar

            double dipHeight = workArea.Height / scaling - frameOverhead;
            double dipWidth = Math.Max(600, workArea.Width / scaling * 0.6);
            Height = dipHeight;
            Width = dipWidth;
            Position = new PixelPoint(
                workArea.X + (int)((workArea.Width - dipWidth * scaling) / 2),
                workArea.Y);
        }
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.PdfLoaded += OnPdfLoaded;

        // Warm up the StorageProvider so the first file-open dialog is fast.
        // On Linux/WSL this forces the D-Bus portal connection to initialize in the background.
        _ = StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Documents);
    }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.PdfLoaded -= OnPdfLoaded;
        _rerenderTimer?.Stop();
        _rerenderTimer = null;
        _scrollTimer?.Stop();
        _scrollTimer = null;
        _rerenderCts?.Cancel();
        _rerenderCts?.Dispose();
        _rerenderCts = null;
        HideTextEditor();
        base.OnClosed(e);
    }

    // ═══ ViewModel tracking ═══

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedAnnotation))
        {
            UpdateTextEditor();
        }
    }

    // ═══ Zoom ═══

    private void OnScrollWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        double step = e.Delta.Y > 0 ? ZoomStep : -ZoomStep;
        var cursorInViewport = e.GetPosition(PdfScrollViewer);
        ApplyZoom(Math.Clamp(_zoom + step, MinZoom, MaxZoom), cursorInViewport);
        e.Handled = true;
    }

    private void ApplyZoom(double level, Point? anchor = null)
    {
        double oldZoom = _zoom;
        _zoom = level;

        // Compute content point under anchor before zoom
        double anchorContentX = 0, anchorContentY = 0;
        double anchorX = 0, anchorY = 0;
        if (anchor is { } pt)
        {
            anchorX = pt.X;
            anchorY = pt.Y;
            anchorContentX = (PdfScrollViewer.Offset.X + anchorX) / oldZoom;
            anchorContentY = (PdfScrollViewer.Offset.Y + anchorY) / oldZoom;
        }

        ZoomTransform.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        ViewModel.ButtonScale = 1.0 / _zoom;
        ViewModel.InsertGapHeight = Math.Ceiling(28.0 / _zoom);
        ViewModel.ZoomPercent = (int)Math.Round(_zoom * 100);
        ViewModel.UpdateStatusText();

        // Adjust scroll so content point stays under anchor
        if (anchor is not null)
        {
            // Force layout so ScrollViewer extent updates
            ZoomTransform.UpdateLayout();
            double newOffX = anchorContentX * _zoom - anchorX;
            double newOffY = anchorContentY * _zoom - anchorY;
            PdfScrollViewer.Offset = new Vector(
                Math.Max(0, newOffX),
                Math.Max(0, newOffY));
        }

        ScheduleRerender();
    }

    // Fixed arrow column width in layout units (scales with zoom like the page)
    private const double ArrowColumnLayoutWidth = 34;

    public void FitToWidth()
    {
        if (ViewModel.Pages.Count == 0) return;

        double available = PdfScrollViewer.Bounds.Width - 40;
        if (available <= 0) return;

        // Total layout width = page + both arrow columns; all scale together with zoom
        double pageScreenWidth = ViewModel.Pages[0].WidthPt * MainViewModel.DpiScale;
        double totalLayoutWidth = pageScreenWidth + 2 * ArrowColumnLayoutWidth;
        if (totalLayoutWidth <= 0) return;

        // Preserve relative vertical position through the zoom change
        double oldExtentH = PdfScrollViewer.Extent.Height;
        double relativeY = oldExtentH > 0
            ? (PdfScrollViewer.Offset.Y + PdfScrollViewer.Viewport.Height / 2) / oldExtentH
            : 0;

        var viewportCenter = new Point(
            PdfScrollViewer.Viewport.Width / 2,
            PdfScrollViewer.Viewport.Height / 2);
        ApplyZoom(Math.Clamp(available / totalLayoutWidth, MinZoom, MaxZoom), viewportCenter);
    }

    // ═══ Adaptive DPI re-render ═══

    private void ScheduleRerender()
    {
        int dpi = QuantizeDpi((int)(MainViewModel.RenderDpi * _zoom * _screenScaling));
        if (dpi == _targetDpi) return;
        _targetDpi = dpi;

        // Cancel any in-flight render — disposal happens in RerenderVisibleAsync
        _rerenderCts?.Cancel();

        // Restart the debounce timer; render fires only after zooming stops
        if (_rerenderTimer == null)
        {
            _rerenderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _rerenderTimer.Tick += (_, _) =>
            {
                _rerenderTimer.Stop();
                _ = RerenderVisibleAsync();
            };
        }
        else
        {
            _rerenderTimer.Stop();
        }
        _rerenderTimer.Start();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // After scrolling stops, re-render any newly visible pages that are stale
        if (_scrollTimer == null)
        {
            _scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _scrollTimer.Tick += (_, _) =>
            {
                _scrollTimer.Stop();
                _ = RerenderVisibleAsync();
            };
        }
        else
        {
            _scrollTimer.Stop();
        }
        _scrollTimer.Start();
    }

    private static int QuantizeDpi(int dpi)
    {
        // Snap to fixed steps to reduce re-render frequency,
        // but scale without a hard cap so vector PDFs stay sharp at any zoom.
        if (dpi <= 100) return 96;
        if (dpi <= 150) return 150;
        if (dpi <= 225) return 200;
        if (dpi <= 350) return 300;
        if (dpi <= 500) return 400;
        if (dpi <= 700) return 600;
        if (dpi <= 1000) return 800;
        return 1200;
    }

    private HashSet<int> GetVisiblePageIndices()
    {
        var visible = new HashSet<int>();
        if (ViewModel.Pages.Count == 0) return visible;

        var itemsControl = (ItemsControl)ZoomTransform.Child!;
        double viewportH = PdfScrollViewer.Viewport.Height;

        for (int i = 0; i < ViewModel.Pages.Count; i++)
        {
            var container = itemsControl.ContainerFromIndex(i);
            if (container == null) continue;

            // TranslatePoint gives viewport-relative coordinates
            // (scroll offset and zoom already applied)
            var top = container.TranslatePoint(new Point(0, 0), PdfScrollViewer);
            var bottom = container.TranslatePoint(new Point(0, container.Bounds.Height), PdfScrollViewer);
            if (top == null || bottom == null) continue;

            if (bottom.Value.Y >= 0 && top.Value.Y <= viewportH)
                visible.Add(i);
        }
        return visible;
    }

    private async Task RerenderVisibleAsync()
    {
        _rerenderCts?.Cancel();
        _rerenderCts?.Dispose();
        var cts = new CancellationTokenSource();
        _rerenderCts = cts;

        var visible = GetVisiblePageIndices();
        int dpi = _targetDpi;

        // Re-render pages that are visible AND not already at the target DPI
        var pagesToRender = ViewModel.Pages
            .Where(p => visible.Contains(p.Index)
                     && (!_pageDpi.TryGetValue(p.Index, out var cur) || cur != dpi))
            .Select(p => (Page: p, p.Source.PdfBytes, p.Source.SourcePageIndex))
            .ToList();

        foreach (var (page, pdfBytes, srcIdx) in pagesToRender)
        {
            if (cts.Token.IsCancellationRequested) return;

            var bitmap = await Task.Run(
                () => PdfRenderService.RenderPage(pdfBytes, srcIdx, dpi),
                cts.Token);

            if (cts.Token.IsCancellationRequested)
            {
                bitmap.Dispose();
                return;
            }

            var old = page.Bitmap;
            page.Bitmap = bitmap;
            old?.Dispose();
            _pageDpi[page.Index] = dpi;
        }

        // Re-render annotation bitmaps on visible pages at the target DPI
        var annotations = ViewModel.Pages
            .Where(p => visible.Contains(p.Index))
            .SelectMany(p => p.Annotations)
            .OfType<SvgAnnotation>()
            .Where(a => a.RenderedBitmap != null && a.RenderedDpi != dpi)
            .ToList();

        foreach (var ann in annotations)
        {
            if (cts.Token.IsCancellationRequested) return;

            Avalonia.Media.Imaging.Bitmap bitmap;
            if (ann.IsRaster)
            {
                bitmap = await Task.Run(
                    () => SvgRenderService.ResampleForDisplay(
                        ann.SvgFilePath, ann.WidthPt, ann.HeightPt, dpi),
                    cts.Token);
            }
            else
            {
                bitmap = await Task.Run(
                    () => SvgRenderService.RenderForDisplay(ann.SvgFilePath, ann.Scale, dpi),
                    cts.Token);
            }

            if (cts.Token.IsCancellationRequested)
            {
                bitmap.Dispose();
                return;
            }

            var old = ann.RenderedBitmap;
            ann.RenderedBitmap = bitmap;
            ann.RenderedDpi = dpi;
            old?.Dispose();
        }
    }


    // ═══ Inline text editor ═══

    private void UpdateTextEditor()
    {
        if (ViewModel.SelectedAnnotation is TextAnnotation text)
            ShowTextEditor(text);
        else
            HideTextEditor();
    }

    private void ShowTextEditor(TextAnnotation text)
    {
        // Detach old
        if (_editingText != null)
        {
            _editingText.PropertyChanged -= OnEditingAnnotationMoved;
            InlineTextBox.TextChanged -= OnInlineTextChanged;
            InlineFontCombo.SelectionChanged -= OnInlineFontChanged;
        }

        _editingText = text;

        // Set values
        InlineTextBox.Text = text.Text;
        InlineFontCombo.SelectedItem = text.FontFamily;

        // Attach live bindings
        InlineTextBox.TextChanged += OnInlineTextChanged;
        InlineFontCombo.SelectionChanged += OnInlineFontChanged;
        _editingText.PropertyChanged += OnEditingAnnotationMoved;

        // Position and show
        PositionEditorOverlay();
        TextEditorOverlay.IsVisible = true;
        InlineTextBox.Focus();
        InlineTextBox.SelectAll();
    }

    private void HideTextEditor()
    {
        if (_editingText != null)
        {
            _editingText.PropertyChanged -= OnEditingAnnotationMoved;
            InlineTextBox.TextChanged -= OnInlineTextChanged;
            InlineFontCombo.SelectionChanged -= OnInlineFontChanged;
            _editingText = null;
        }
        TextEditorOverlay.IsVisible = false;
    }

    private void OnInlineTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_editingText != null && InlineTextBox.Text != null)
            _editingText.Text = InlineTextBox.Text;
    }

    private void OnInlineFontChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_editingText != null && InlineFontCombo.SelectedItem is string font)
            _editingText.FontFamily = font;
    }

    private void OnEditingAnnotationMoved(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "X" or "Y" or "HeightPt")
            PositionEditorOverlay();
    }

    private void PositionEditorOverlay()
    {
        if (_editingText == null) return;

        double annScreenX = _editingText.X * MainViewModel.DpiScale;
        double annScreenBottom = (_editingText.Y + _editingText.HeightPt) * MainViewModel.DpiScale + 8;

        // Find the PageCanvas hosting this annotation
        var canvas = FindCanvasForAnnotation(_editingText);
        if (canvas == null) return;

        var transform = canvas.TransformToVisual(this);
        if (transform == null) return;

        var pos = transform.Value.Transform(new Point(annScreenX, annScreenBottom));

        // Clamp within window bounds
        pos = new Point(
            Math.Clamp(pos.X, 50, Math.Max(50, Bounds.Width - 230)),
            Math.Clamp(pos.Y, 0, Math.Max(0, Bounds.Height - 100)));

        TextEditorOverlay.Margin = new Thickness(pos.X, pos.Y, 0, 0);
    }

    private PageCanvas? FindCanvasForAnnotation(Annotation ann)
    {
        var items = PdfScrollViewer.Content as LayoutTransformControl;
        var itemsControl = items?.Child as ItemsControl;
        if (itemsControl == null) return null;

        foreach (var container in itemsControl.GetRealizedContainers())
        {
            var canvas = FindDescendant<PageCanvas>(container);
            if (canvas?.Annotations?.Contains(ann) == true)
                return canvas;
        }
        return null;
    }

    private static T? FindDescendant<T>(Control root) where T : Control
    {
        if (root is T match) return match;
        if (root is ContentPresenter cp && cp.Child is Control cpChild)
            return FindDescendant<T>(cpChild);
        if (root is Decorator d && d.Child is Control dChild)
            return FindDescendant<T>(dChild);
        if (root is Panel p)
        {
            foreach (var child in p.Children)
            {
                if (child is Control cc)
                {
                    var found = FindDescendant<T>(cc);
                    if (found != null) return found;
                }
            }
        }
        return null;
    }

    // ═══ Drag-and-drop ═══

    private static bool HasPdfFiles(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        return files?.Any(f => f.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) == true;
    }

    private int _dragEnterCount;

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (HasPdfFiles(e))
        {
            _dragEnterCount++;
            ViewModel.IsDraggingFile = true;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;
        if (HasPdfFiles(e))
            e.DragEffects = DragDropEffects.Copy;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (--_dragEnterCount <= 0)
        {
            _dragEnterCount = 0;
            ViewModel.IsDraggingFile = false;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        _dragEnterCount = 0;
        ViewModel.IsDraggingFile = false;

        var files = e.DataTransfer.TryGetFiles();
        var pdfFile = files?.FirstOrDefault(f => f.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        var path = pdfFile?.TryGetLocalPath();
        if (path == null) return;

        if (ViewModel.Pages.Count == 0)
        {
            ViewModel.LoadPdf(path);
        }
        else
        {
            int insertIndex = GetInsertionIndex(e);
            ViewModel.InsertPagesFromFile(path, insertIndex);
        }
    }

    private int GetInsertionIndex(DragEventArgs e)
    {
        if (ViewModel.Pages.Count == 0) return 0;

        var dropPos = e.GetPosition(PdfScrollViewer);
        var itemsControl = (ItemsControl)ZoomTransform.Child!;

        for (int i = 0; i < ViewModel.Pages.Count; i++)
        {
            var container = itemsControl.ContainerFromIndex(i);
            if (container == null) continue;

            // Translate container midpoint to ScrollViewer coordinates
            var mid = new Point(0, container.Bounds.Height / 2);
            var inScrollViewer = container.TranslatePoint(mid, PdfScrollViewer);
            if (inScrollViewer == null) continue;

            if (dropPos.Y < inScrollViewer.Value.Y)
                return i;
        }

        return ViewModel.Pages.Count;
    }

    // ═══ Fit-to-width on load ═══

    private void OnPdfLoaded()
    {
        // Mark all pages as rendered at the base DPI (initial load renders at RenderDpi)
        _pageDpi.Clear();
        _targetDpi = MainViewModel.RenderDpi;
        foreach (var page in ViewModel.Pages)
            _pageDpi[page.Index] = MainViewModel.RenderDpi;

        // Delay until layout has completed so FitToWidth can measure page widths
        Dispatcher.UIThread.Post(FitToWidth, DispatcherPriority.Background);
    }

    // ═══ Keyboard ═══

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Don't intercept when editing text
        if (TextEditorOverlay.IsVisible && InlineTextBox.IsFocused)
        {
            if (e.Key == Key.Escape)
            {
                HideTextEditor();
                e.Handled = true;
            }
            return;
        }

        base.OnKeyDown(e);

        if (e.Key == Key.Delete || e.Key == Key.Back)
        {
            if (ViewModel.DeleteCommand.CanExecute(null))
            {
                HideTextEditor();
                ViewModel.DeleteCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            HideTextEditor();
            ViewModel.CurrentTool = ToolMode.Select;
            ViewModel.SelectAnnotation(null);
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.D0 || e.Key == Key.NumPad0)
            { FitToWidth(); e.Handled = true; }
            else if (e.Key == Key.OemPlus || e.Key == Key.Add)
            { ApplyZoom(Math.Clamp(_zoom + ZoomStep, MinZoom, MaxZoom)); e.Handled = true; }
            else if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
            { ApplyZoom(Math.Clamp(_zoom - ZoomStep, MinZoom, MaxZoom)); e.Handled = true; }
        }
    }

    // ═══ Canvas events ═══

    private void OnCanvasClicked(object? sender, CanvasClickedEventArgs e)
    {
        HideTextEditor();
        ViewModel.OnCanvasClicked(e.PageIndex, e.PdfX, e.PdfY);
    }

    private void OnAnnotationSelected(object? sender, AnnotationSelectedEventArgs e)
    {
        ViewModel.SelectAnnotation(e.Annotation);
    }

    private void OnDeleteRequested(object? sender, RoutedEventArgs e)
    {
        HideTextEditor();
        if (ViewModel.DeleteCommand.CanExecute(null))
            ViewModel.DeleteCommand.Execute(null);
    }

    // ═══ Zoom buttons ═══

    private void OnFitToWidth(object? sender, RoutedEventArgs e) => FitToWidth();
    private void OnZoomIn(object? sender, RoutedEventArgs e) => ApplyZoom(Math.Clamp(_zoom + ZoomStep, MinZoom, MaxZoom));
    private void OnZoomOut(object? sender, RoutedEventArgs e) => ApplyZoom(Math.Clamp(_zoom - ZoomStep, MinZoom, MaxZoom));

    // ═══ Theme toggle ═══

    private bool _isDark = true; // dark by default

    private void OnThemeToggle(object? sender, RoutedEventArgs e)
    {
        _isDark = !_isDark;
        Application.Current!.RequestedThemeVariant = _isDark ? ThemeVariant.Dark : ThemeVariant.Light;

        ThemeIcon.Data = IconService.CreateGeometry(_isDark ? Iconr.Icon.sun : Iconr.Icon.moon);
    }
}
