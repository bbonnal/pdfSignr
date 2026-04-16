using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
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
    private string? _textBeforeEdit;
    private string? _fontBeforeEdit;
    private double _zoom = 1.0;
    private double _screenScaling = 1.0;
    private int _targetDpi = PdfConstants.RenderDpi;
    private readonly Dictionary<PageItem, int> _pageDpi = new(); // page → DPI last rendered at
    private DispatcherTimer? _rerenderTimer;
    private DispatcherTimer? _scrollTimer;
    private CancellationTokenSource? _rerenderCts;
    private CancellationTokenSource? _backgroundLoadCts;

    // Page drag-to-reorder state
    private const double DragThreshold = 8;
    private bool _pageDragPending;
    private bool _pageDragging;
    private PageItem? _dragSourcePage;
    private List<PageItem>? _dragSourcePages; // non-null when dragging a multi-selection
    private Point _dragStartPos;
    private Border? _dragAdorner;
    private int _dropTargetIndex = -1;

    public MainWindow()
    {
        InitializeComponent();

        AddHandler(PageCanvas.CanvasClickedEvent, OnCanvasClicked);
        AddHandler(PageCanvas.AnnotationSelectedEvent, OnAnnotationSelected);
        AddHandler(PageCanvas.DeleteRequestedEvent, OnDeleteRequested);
        AddHandler(PageCanvas.AnnotationManipulatedEvent, OnAnnotationManipulated);
        // Tunnel handler for page selection — fires before PageCanvas consumes the event
        AddHandler(PointerPressedEvent, OnPageAreaPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPageAreaPointerMoved, RoutingStrategies.Tunnel);
        // Tunnel so we get the event before ScrollViewer consumes it
        PdfScrollViewer.AddHandler(PointerWheelChangedEvent, OnScrollWheel, RoutingStrategies.Tunnel);

        // Re-render newly visible pages after scrolling stops
        PdfScrollViewer.ScrollChanged += OnScrollChanged;

        // Drag-and-drop for PDF files — accept drops anywhere in the window
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        PopulateKeyBindingsFlyout();
    }

    private void PopulateKeyBindingsFlyout()
    {
        string? lastCategory = null;
        foreach (var kb in Services.KeyBindingService.Bindings)
        {
            if (kb.Category != lastCategory)
            {
                lastCategory = kb.Category;
                KeyBindingsFlyout.Children.Add(new TextBlock
                {
                    Text = kb.Category,
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Opacity = 0.6,
                    Margin = new Thickness(0, lastCategory == Services.KeyBindingService.Bindings[0].Category ? 0 : 8, 0, 2)
                });
            }

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            };
            var keyBorder = new Border
            {
                Background = Avalonia.Media.Brushes.Gray,
                Opacity = 0.15,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1),
                Margin = new Thickness(0, 0, 10, 0),
                Child = new TextBlock
                {
                    Text = kb.Keys,
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                }
            };
            var desc = new TextBlock
            {
                Text = kb.Description,
                FontSize = 11,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            Grid.SetColumn(keyBorder, 0);
            Grid.SetColumn(desc, 1);
            row.Children.Add(keyBorder);
            row.Children.Add(desc);
            KeyBindingsFlyout.Children.Add(row);
        }
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
        ViewModel.PageStructureChanged += OnPageStructureChanged;
        ViewModel.PageRotated += OnPageRotated;

        // Warm up the StorageProvider so the first file-open dialog is fast.
        // On Linux/WSL this forces the D-Bus portal connection to initialize in the background.
        _ = StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Documents);
    }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.PdfLoaded -= OnPdfLoaded;
        ViewModel.PageStructureChanged -= OnPageStructureChanged;
        ViewModel.PageRotated -= OnPageRotated;
        _rerenderTimer?.Stop();
        _rerenderTimer = null;
        _scrollTimer?.Stop();
        _scrollTimer = null;
        _rerenderCts?.Cancel();
        _rerenderCts?.Dispose();
        _rerenderCts = null;
        _backgroundLoadCts?.Cancel();
        _backgroundLoadCts?.Dispose();
        _backgroundLoadCts = null;
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

    private void OnPageStructureChanged()
    {
        // Remove entries for pages no longer in the document
        var current = new HashSet<PageItem>(ViewModel.Pages);
        foreach (var key in _pageDpi.Keys.Where(k => !current.Contains(k)).ToList())
            _pageDpi.Remove(key);
    }

    private async void OnPageRotated(PageItem page)
    {
        int dpi = _targetDpi;
        try
        {
            var bitmap = await Task.Run(() =>
                PdfRenderService.RenderPage(
                    page.Source.PdfBytes, page.Source.SourcePageIndex,
                    dpi, page.RotationDegrees));
            page.ReplaceBitmap(bitmap);
            _pageDpi[page] = dpi;
        }
        catch (Exception) { }
    }

    private void OnPageAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        // Walk up from the source to find a PageItem DataContext
        var source = e.Source as Control;
        var page = source != null ? FindPageItemFromControl(source) : null;
        if (page == null)
        {
            if (ViewModel.HasSelectedPages)
                ViewModel.ClearPageSelection();
            return;
        }

        // Don't change selection when clicking per-page action buttons or drag handle
        if (IsPageActionControl(source))
            return;

        // Clicking on the selection border zone of a selected page → initiate drag
        if (page.IsSelected)
        {
            var selBorder = FindSelectionBorder(source);
            if (selBorder != null && IsInBorderZone(e.GetPosition(selBorder), selBorder.Bounds))
            {
                BeginPageDragFromBorder(page, selBorder, e);
                return;
            }
        }

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // Only update selection on Ctrl/Shift clicks, or plain clicks (don't set Handled — let annotation interaction proceed)
        if (ctrl || shift)
            ViewModel.SelectPage(page, ctrl, shift);
        else if (!page.IsSelected || ViewModel.SelectedPageCount > 1)
        {
            // Plain click on unselected page or multi-selected: exclusive select
            ViewModel.SelectPage(page, false, false);
        }
    }

    private static bool IsPageActionControl(Control? source)
    {
        Control? current = source;
        while (current != null)
        {
            if (current is Button btn && btn.Classes.Contains("insert-btn"))
                return true;
            current = current.Parent as Control;
        }
        return false;
    }

    private static Border? FindSelectionBorder(Control? source)
    {
        Control? current = source;
        while (current != null)
        {
            if (current is Border b && b.Classes.Contains("page-select"))
                return b;
            current = current.Parent as Control;
        }
        return null;
    }

    private bool IsInBorderZone(Point pos, Rect bounds)
    {
        // 8 physical pixels hit zone around the border edge
        double hitZone = 8.0 / _zoom;
        return pos.X < hitZone || pos.X > bounds.Width - hitZone
            || pos.Y < hitZone || pos.Y > bounds.Height - hitZone;
    }

    private void BeginPageDragFromBorder(PageItem page, Border border, PointerPressedEventArgs e)
    {
        _pageDragPending = true;
        _pageDragging = false;
        _dragSourcePage = page;
        _dragSourcePages = ViewModel.SelectedPageCount > 1
            ? ViewModel.SelectedPages.ToList()
            : null;
        _dragStartPos = e.GetPosition(this);
        _dropTargetIndex = -1;

        e.Pointer.Capture(border);
        border.PointerMoved += OnDragHandleMoved;
        border.PointerReleased += OnDragHandleReleased;
        e.Handled = true;
    }

    private static readonly Cursor BorderDragCursor = new(StandardCursorType.Hand);
    private Border? _lastCursorBorder;

    private void OnPageAreaPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pageDragging) return; // don't interfere while dragging

        var source = e.Source as Control;
        var page = source != null ? FindPageItemFromControl(source) : null;

        if (page is { IsSelected: true })
        {
            var selBorder = FindSelectionBorder(source);
            if (selBorder != null && IsInBorderZone(e.GetPosition(selBorder), selBorder.Bounds))
            {
                if (_lastCursorBorder != selBorder)
                {
                    ClearBorderCursor();
                    selBorder.Cursor = BorderDragCursor;
                    _lastCursorBorder = selBorder;
                }
                return;
            }
        }

        ClearBorderCursor();
    }

    private void ClearBorderCursor()
    {
        if (_lastCursorBorder != null)
        {
            _lastCursorBorder.Cursor = null;
            _lastCursorBorder = null;
        }
    }

    private void OnAnnotationManipulated(object? sender, AnnotationManipulatedEventArgs e)
    {
        var ann = e.Annotation;
        double oldX = e.OldX, oldY = e.OldY, oldW = e.OldW, oldH = e.OldH, oldRot = e.OldRot;
        double newX = e.NewX, newY = e.NewY, newW = e.NewW, newH = e.NewH, newRot = e.NewRot;

        ViewModel.UndoRedo.Push(new Services.UndoEntry(
            "Move/resize annotation",
            Undo: () =>
            {
                ann.X = oldX; ann.Y = oldY; ann.WidthPt = oldW; ann.HeightPt = oldH; ann.Rotation = oldRot;
                if (ann is SvgAnnotation svg)
                {
                    svg.Scale = svg.OriginalWidthPt > 0 ? oldW / svg.OriginalWidthPt : 1;
                    svg.ReRender(PdfConstants.RenderDpi);
                }
            },
            Redo: () =>
            {
                ann.X = newX; ann.Y = newY; ann.WidthPt = newW; ann.HeightPt = newH; ann.Rotation = newRot;
                if (ann is SvgAnnotation svg)
                {
                    svg.Scale = svg.OriginalWidthPt > 0 ? newW / svg.OriginalWidthPt : 1;
                    svg.ReRender(PdfConstants.RenderDpi);
                }
            }
        ));
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
        ViewModel.SelectionBorderThickness = new Thickness(3.0 / _zoom);
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
        double pageScreenWidth = ViewModel.Pages[0].WidthPt * PdfConstants.DpiScale;
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
        int dpi = QuantizeDpi((int)(PdfConstants.RenderDpi * _zoom * _screenScaling));
        if (dpi == _targetDpi) return;
        _targetDpi = dpi;

        // Cancel any in-flight render — disposal happens in RerenderVisibleAsync
        _rerenderCts?.Cancel();

        RestartDebounce(ref _rerenderTimer, TimeSpan.FromMilliseconds(200),
            () => _ = RerenderVisibleAsync());
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        RestartDebounce(ref _scrollTimer, TimeSpan.FromMilliseconds(200),
            () => _ = RerenderVisibleAsync());
    }

    private static void RestartDebounce(ref DispatcherTimer? timer, TimeSpan interval, Action callback)
    {
        if (timer == null)
        {
            var t = new DispatcherTimer { Interval = interval };
            t.Tick += (_, _) => { t.Stop(); callback(); };
            timer = t;
        }
        else
        {
            timer.Stop();
        }
        timer.Start();
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
        bool foundVisible = false;

        for (int i = 0; i < ViewModel.Pages.Count; i++)
        {
            var container = itemsControl.ContainerFromIndex(i);
            if (container == null) continue;

            var top = container.TranslatePoint(new Point(0, 0), PdfScrollViewer);
            var bottom = container.TranslatePoint(new Point(0, container.Bounds.Height), PdfScrollViewer);
            if (top == null || bottom == null) continue;

            if (bottom.Value.Y >= 0 && top.Value.Y <= viewportH)
            {
                visible.Add(i);
                foundVisible = true;
            }
            else if (foundVisible && !ViewModel.IsGridMode)
            {
                break; // pages are vertical — all remaining are below viewport
            }
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
                     && (!_pageDpi.TryGetValue(p, out var cur) || cur != dpi))
            .Select(p => (Page: p, p.Source.PdfBytes, p.Source.SourcePageIndex, p.RotationDegrees))
            .ToList();

        // Render in parallel — each page is independent.
        // Continuations run on the UI thread so bitmap swaps are safe.
        var renderTasks = pagesToRender.Select(async item =>
        {
            var (page, pdfBytes, srcIdx, rotDeg) = item;
            try
            {
                var bitmap = await Task.Run(
                    () => PdfRenderService.RenderPage(pdfBytes, srcIdx, dpi, rotDeg),
                    cts.Token);

                if (cts.Token.IsCancellationRequested)
                {
                    bitmap.Dispose();
                    return;
                }

                page.ReplaceBitmap(bitmap);
                _pageDpi[page] = dpi;
            }
            catch (OperationCanceledException) { }
        });

        try { await Task.WhenAll(renderTasks); }
        catch (OperationCanceledException) { return; }

        if (cts.Token.IsCancellationRequested) return;

        // Re-render annotation bitmaps on visible pages at the target DPI
        var annotations = ViewModel.Pages
            .Where(p => visible.Contains(p.Index))
            .SelectMany(p => p.Annotations)
            .OfType<SvgAnnotation>()
            .Where(a => a.RenderedBitmap != null && a.RenderedDpi != dpi)
            .ToList();

        var annTasks = annotations.Select(async ann =>
        {
            try
            {
                if (cts.Token.IsCancellationRequested) return;
                await Task.Run(() => ann.ReRender(dpi), cts.Token);
            }
            catch (OperationCanceledException) { }
        });

        try { await Task.WhenAll(annTasks); }
        catch (OperationCanceledException) { }
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
        _textBeforeEdit = text.Text;
        _fontBeforeEdit = text.FontFamily;

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
            // Push undo entry if text or font changed
            var ann = _editingText;
            var oldText = _textBeforeEdit;
            var oldFont = _fontBeforeEdit;
            var newText = ann.Text;
            var newFont = ann.FontFamily;

            if (oldText != newText || oldFont != newFont)
            {
                ViewModel.UndoRedo.Push(new Services.UndoEntry(
                    "Edit text",
                    Undo: () => { ann.Text = oldText!; ann.FontFamily = oldFont!; },
                    Redo: () => { ann.Text = newText; ann.FontFamily = newFont; }
                ));
            }

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

        double annScreenX = _editingText.X * PdfConstants.DpiScale;
        double annScreenBottom = (_editingText.Y + _editingText.HeightPt) * PdfConstants.DpiScale + 8;

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

    // ═══ Page drag-to-reorder ═══

    private void OnDragHandleMoved(object? sender, PointerEventArgs e)
    {
        if (!_pageDragPending || _dragSourcePage == null) return;

        var pos = e.GetPosition(this);
        var delta = pos - _dragStartPos;

        if (!_pageDragging)
        {
            if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
                return;
            _pageDragging = true;
            ViewModel.IsDraggingPage = true;
            CreateDragAdorner(pos);
        }

        // Move adorner
        if (_dragAdorner != null)
        {
            _dragAdorner.Margin = new Thickness(
                pos.X - _dragAdorner.Width / 2,
                pos.Y - _dragAdorner.Height / 2, 0, 0);
        }

        // Hit-test to find drop target
        UpdateDropTarget(e.GetPosition(PdfScrollViewer));
    }

    private void OnDragHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Control handle)
        {
            handle.PointerMoved -= OnDragHandleMoved;
            handle.PointerReleased -= OnDragHandleReleased;
            e.Pointer.Capture(null);
        }

        if (_pageDragging && _dragSourcePage != null && _dropTargetIndex >= 0)
        {
            if (_dragSourcePages != null)
            {
                ViewModel.MovePagesByDrag(_dragSourcePages, _dropTargetIndex);
            }
            else
            {
                int fromIndex = ViewModel.Pages.IndexOf(_dragSourcePage);
                int toIndex = _dropTargetIndex;
                // Adjust target: if moving forward, the removal shifts indices
                if (fromIndex < toIndex) toIndex--;
                if (fromIndex >= 0 && toIndex >= 0 && toIndex < ViewModel.Pages.Count && fromIndex != toIndex)
                {
                    ViewModel.MovePageByDrag(fromIndex, toIndex);
                }
            }
        }

        RemoveDragAdorner();
        ViewModel.ClearDropTargets();
        _pageDragPending = false;
        _pageDragging = false;
        _dragSourcePage = null;
        _dragSourcePages = null;
        _dropTargetIndex = -1;
        ViewModel.IsDraggingPage = false;
        e.Handled = true;
    }

    private void CreateDragAdorner(Point pos)
    {
        if (_dragSourcePage?.Bitmap == null) return;

        // Create a small thumbnail of the page
        double thumbW = 80;
        double thumbH = thumbW * (_dragSourcePage.HeightPt / _dragSourcePage.WidthPt);

        var image = new Avalonia.Controls.Image
        {
            Source = _dragSourcePage.Bitmap,
            Width = thumbW,
            Height = thumbH
        };

        Control child;
        if (_dragSourcePages != null && _dragSourcePages.Count > 1)
        {
            // Multi-page drag: show thumbnail with count badge
            var badge = new Border
            {
                Background = Brushes.DodgerBlue,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6, 2),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Margin = new Thickness(0, -8, -8, 0),
                Child = new TextBlock
                {
                    Text = _dragSourcePages.Count.ToString(),
                    Foreground = Brushes.White,
                    FontSize = 11,
                    FontWeight = FontWeight.Bold
                }
            };
            var grid = new Grid { Width = thumbW, Height = thumbH };
            grid.Children.Add(image);
            grid.Children.Add(badge);
            child = grid;
        }
        else
        {
            child = image;
        }

        _dragAdorner = new Border
        {
            Child = child,
            Background = Brushes.White,
            BoxShadow = new BoxShadows(new BoxShadow { OffsetX = 0, OffsetY = 2, Blur = 8, Color = new Color(100, 0, 0, 0) }),
            Opacity = 0.8,
            Width = thumbW,
            Height = thumbH,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            IsHitTestVisible = false,
            Margin = new Thickness(pos.X - thumbW / 2, pos.Y - thumbH / 2, 0, 0)
        };

        // Add to the top-level Panel (parent of DockPanel)
        if (Content is Panel rootPanel)
        {
            rootPanel.Children.Add(_dragAdorner);
        }
    }

    private void RemoveDragAdorner()
    {
        if (Content is Panel rootPanel)
        {
            if (_dragAdorner != null) rootPanel.Children.Remove(_dragAdorner);
        }
        _dragAdorner = null;
    }

    private void UpdateDropTarget(Point posInScrollViewer)
    {
        _dropTargetIndex = FindInsertionIndex(posInScrollViewer);
        ViewModel.SetDropTarget(_dropTargetIndex);
    }

    private static PageItem? FindPageItemFromControl(Control control)
    {
        // Walk up the visual/logical tree to find the PageItem DataContext
        Control? current = control;
        while (current != null)
        {
            if (current.DataContext is PageItem page)
                return page;
            current = current.Parent as Control;
        }
        return null;
    }

    // ═══ Drag-and-drop (file) ═══

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
            _ = ViewModel.LoadPdfAsync(path);
        }
        else
        {
            int insertIndex = FindInsertionIndex(e.GetPosition(PdfScrollViewer));
            _ = ViewModel.InsertPagesFromFileAsync(path, insertIndex);
        }
    }

    private int FindInsertionIndex(Point posInScrollViewer)
    {
        if (ViewModel.Pages.Count == 0) return 0;

        var itemsControl = (ItemsControl)ZoomTransform.Child!;
        int bestIndex = ViewModel.Pages.Count;
        double minDist = double.MaxValue;

        for (int i = 0; i < ViewModel.Pages.Count; i++)
        {
            var container = itemsControl.ContainerFromIndex(i);
            if (container == null) continue;

            var topLeft = container.TranslatePoint(new Point(0, 0), PdfScrollViewer);
            var bottomRight = container.TranslatePoint(
                new Point(container.Bounds.Width, container.Bounds.Height), PdfScrollViewer);
            if (topLeft == null || bottomRight == null) continue;

            double midX = (topLeft.Value.X + bottomRight.Value.X) / 2;
            double midY = (topLeft.Value.Y + bottomRight.Value.Y) / 2;

            if (ViewModel.IsGridMode)
            {
                double dist = (posInScrollViewer.X - midX) * (posInScrollViewer.X - midX)
                            + (posInScrollViewer.Y - midY) * (posInScrollViewer.Y - midY);
                if (dist < minDist)
                {
                    minDist = dist;
                    bestIndex = posInScrollViewer.X < midX ? i : i + 1;
                }
            }
            else
            {
                double dist = Math.Abs(posInScrollViewer.Y - topLeft.Value.Y);
                if (dist < minDist)
                {
                    minDist = dist;
                    bestIndex = i;
                }
                // Also check bottom of last page
                if (i == ViewModel.Pages.Count - 1)
                {
                    double distBottom = Math.Abs(posInScrollViewer.Y - bottomRight.Value.Y);
                    if (distBottom < minDist)
                    {
                        bestIndex = i + 1;
                    }
                }
            }
        }

        return Math.Min(bestIndex, ViewModel.Pages.Count);
    }

    // ═══ Fit-to-width on load ═══

    private void OnPdfLoaded()
    {
        _backgroundLoadCts?.Cancel();
        _pageDpi.Clear();
        _targetDpi = PdfConstants.RenderDpi;

        // Pages arrive with null bitmaps — render visible pages first, then the rest
        Dispatcher.UIThread.Post(async () =>
        {
            FitToWidth();
            await RerenderVisibleAsync();
            _ = RenderRemainingPagesAsync();
        }, DispatcherPriority.Background);
    }

    private async Task RenderRemainingPagesAsync()
    {
        _backgroundLoadCts?.Cancel();
        _backgroundLoadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _backgroundLoadCts = cts;

        int dpi = _targetDpi;
        var unrendered = ViewModel.Pages
            .Where(p => !_pageDpi.ContainsKey(p))
            .ToList();

        foreach (var page in unrendered)
        {
            if (cts.Token.IsCancellationRequested) return;
            if (_pageDpi.ContainsKey(page)) continue;

            try
            {
                var bitmap = await Task.Run(
                    () => PdfRenderService.RenderPage(
                        page.Source.PdfBytes, page.Source.SourcePageIndex, dpi, page.RotationDegrees),
                    cts.Token);

                if (cts.Token.IsCancellationRequested) { bitmap.Dispose(); return; }

                page.ReplaceBitmap(bitmap);
                _pageDpi[page] = dpi;
            }
            catch (OperationCanceledException) { return; }
        }
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
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl)
        {
            if (e.Key == Key.Z && !shift)
            { if (ViewModel.UndoCommand.CanExecute(null)) ViewModel.UndoCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Y || (e.Key == Key.Z && shift))
            { if (ViewModel.RedoCommand.CanExecute(null)) ViewModel.RedoCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.A)
            { ViewModel.SelectAllPages(); e.Handled = true; }
            else if (e.Key == Key.O)
            { ViewModel.OpenCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.S)
            {
                // Save selected pages if selection exists, otherwise save all
                if (ViewModel.HasSelectedPages && ViewModel.SaveSelectedPagesCommand.CanExecute(null))
                    ViewModel.SaveSelectedPagesCommand.Execute(null);
                else if (ViewModel.SaveCommand.CanExecute(null))
                    ViewModel.SaveCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.D0 || e.Key == Key.NumPad0)
            { FitToWidth(); e.Handled = true; }
            else if (e.Key == Key.OemPlus || e.Key == Key.Add)
            { ApplyZoom(Math.Clamp(_zoom + ZoomStep, MinZoom, MaxZoom)); e.Handled = true; }
            else if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
            { ApplyZoom(Math.Clamp(_zoom - ZoomStep, MinZoom, MaxZoom)); e.Handled = true; }
            else if (e.Key == Key.Up)
            { SelectAdjacentPage(-1, addToSelection: true); e.Handled = true; }
            else if (e.Key == Key.Down)
            { SelectAdjacentPage(1, addToSelection: true); e.Handled = true; }
        }
        else if (e.Key == Key.Delete || e.Key == Key.Back)
        {
            // Delete selected annotation first, then fall back to selected pages
            if (ViewModel.DeleteCommand.CanExecute(null))
            {
                HideTextEditor();
                ViewModel.DeleteCommand.Execute(null);
                e.Handled = true;
            }
            else if (ViewModel.HasSelectedPages)
            {
                ViewModel.DeleteSelectedPagesCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            HideTextEditor();
            ViewModel.CurrentTool = ToolMode.Select;
            ViewModel.SelectAnnotation(null);
            if (ViewModel.HasSelectedPages)
                ViewModel.ClearPageSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.R)
        {
            PageItem? target = null;
            if (ViewModel.HasSelectedPages)
                target = ViewModel.SelectedPages.First();
            else
            {
                var visible = GetVisiblePageIndices();
                if (visible.Count > 0)
                    target = ViewModel.Pages[visible.Min()];
            }
            if (target != null)
            {
                if (shift)
                    ViewModel.RotatePageCcwCommand.Execute(target);
                else
                    ViewModel.RotatePageCwCommand.Execute(target);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.M)
        {
            var target = GetSelectedOrVisiblePage();
            if (target != null)
            {
                if (shift)
                    ViewModel.MovePageDownCommand.Execute(target);
                else
                    ViewModel.MovePageUpCommand.Execute(target);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.S)
        {
            ViewModel.ToggleSignToolCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.A)
        {
            ViewModel.ToggleTextToolCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.G)
        {
            ViewModel.IsGridMode = !ViewModel.IsGridMode;
            ApplyGridMode(ViewModel.IsGridMode);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            SelectCenterPage();
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            ScrollByPages(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            ScrollByPages(1);
            e.Handled = true;
        }
        else if (e.Key == Key.D0 || e.Key == Key.NumPad0)
        { FitToWidth(); e.Handled = true; }
        else if (e.Key == Key.OemPlus || e.Key == Key.Add)
        { ApplyZoom(Math.Clamp(_zoom + ZoomStep, MinZoom, MaxZoom)); e.Handled = true; }
        else if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
        { ApplyZoom(Math.Clamp(_zoom - ZoomStep, MinZoom, MaxZoom)); e.Handled = true; }
    }

    private PageItem? GetSelectedOrVisiblePage()
    {
        if (ViewModel.HasSelectedPages)
            return ViewModel.SelectedPages.First();
        var visible = GetVisiblePageIndices();
        return visible.Count > 0 ? ViewModel.Pages[visible.Min()] : null;
    }

    private void SelectCenterPage()
    {
        if (ViewModel.Pages.Count == 0) return;
        var center = new Point(
            PdfScrollViewer.Viewport.Width / 2,
            PdfScrollViewer.Viewport.Height / 2);

        var itemsControl = (ItemsControl)ZoomTransform.Child!;
        int bestIndex = -1;
        double minDist = double.MaxValue;

        for (int i = 0; i < ViewModel.Pages.Count; i++)
        {
            var container = itemsControl.ContainerFromIndex(i);
            if (container == null) continue;
            var top = container.TranslatePoint(new Point(0, 0), PdfScrollViewer);
            var bottom = container.TranslatePoint(
                new Point(container.Bounds.Width, container.Bounds.Height), PdfScrollViewer);
            if (top == null || bottom == null) continue;

            double midY = (top.Value.Y + bottom.Value.Y) / 2;
            double dist = Math.Abs(center.Y - midY);
            if (dist < minDist) { minDist = dist; bestIndex = i; }
        }

        if (bestIndex >= 0)
        {
            ViewModel.ClearPageSelection();
            ViewModel.Pages[bestIndex].IsSelected = true;
            ViewModel.UpdateSelectionState();
        }
    }

    private void ScrollByPages(int direction)
    {
        // Scroll by roughly one page height
        double step = PdfScrollViewer.Viewport.Height * 0.85;
        var offset = PdfScrollViewer.Offset;
        PdfScrollViewer.Offset = new Vector(
            offset.X,
            Math.Max(0, offset.Y + direction * step));
    }

    private void SelectAdjacentPage(int direction, bool addToSelection)
    {
        if (ViewModel.Pages.Count == 0) return;

        // Find the "anchor" — the last selected page or the center page
        int anchorIndex;
        if (ViewModel.HasSelectedPages)
        {
            var selected = ViewModel.Pages
                .Select((p, i) => (p, i))
                .Where(x => x.p.IsSelected)
                .Select(x => x.i);
            anchorIndex = direction > 0 ? selected.Max() : selected.Min();
        }
        else
        {
            // Nothing selected — use center page
            SelectCenterPage();
            return;
        }

        int targetIndex = Math.Clamp(anchorIndex + direction, 0, ViewModel.Pages.Count - 1);
        if (targetIndex == anchorIndex) return;

        if (addToSelection)
            ViewModel.Pages[targetIndex].IsSelected = true;
        else
        {
            ViewModel.ClearPageSelection();
            ViewModel.Pages[targetIndex].IsSelected = true;
        }
        ViewModel.UpdateSelectionState();
        ScrollPageIntoView(targetIndex);
    }

    private void ScrollPageIntoView(int pageIndex)
    {
        var itemsControl = (ItemsControl)ZoomTransform.Child!;
        var container = itemsControl.ContainerFromIndex(pageIndex);
        if (container == null) return;

        var top = container.TranslatePoint(new Point(0, 0), PdfScrollViewer);
        var bottom = container.TranslatePoint(
            new Point(0, container.Bounds.Height), PdfScrollViewer);
        if (top == null || bottom == null) return;

        double viewH = PdfScrollViewer.Viewport.Height;
        if (top.Value.Y < 0)
        {
            // Page is above viewport — scroll up
            PdfScrollViewer.Offset = new Vector(
                PdfScrollViewer.Offset.X,
                PdfScrollViewer.Offset.Y + top.Value.Y - 20);
        }
        else if (bottom.Value.Y > viewH)
        {
            // Page is below viewport — scroll down
            PdfScrollViewer.Offset = new Vector(
                PdfScrollViewer.Offset.X,
                PdfScrollViewer.Offset.Y + bottom.Value.Y - viewH + 20);
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

    // ═══ Grid / list toggle ═══

    private void OnToggleGridMode(object? sender, RoutedEventArgs e)
    {
        ViewModel.IsGridMode = !ViewModel.IsGridMode;
        ApplyGridMode(ViewModel.IsGridMode);
    }

    private void ApplyGridMode(bool gridMode)
    {
        var itemsControl = (ItemsControl)ZoomTransform.Child!;

        if (gridMode)
        {
            PdfScrollViewer.HorizontalScrollBarVisibility =
                Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
            itemsControl.ItemsPanel = new FuncTemplate<Panel?>(() =>
                new WrapPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                });
            GridToggleIcon.Data = IconService.CreateGeometry(Iconr.Icon.rows);
        }
        else
        {
            PdfScrollViewer.HorizontalScrollBarVisibility =
                Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
            itemsControl.ItemsPanel = new FuncTemplate<Panel?>(() =>
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Vertical,
                    Spacing = 0,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                });
            GridToggleIcon.Data = IconService.CreateGeometry(Iconr.Icon.squares_four);
        }

        // Visible pages change when layout switches, schedule re-render
        ScheduleRerender();
    }

    // ═══ Save flyout ═══

    private void OnSaveRangeClick(object? sender, RoutedEventArgs e)
    {
        var text = SaveRangeBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        if (ViewModel.SavePageRangeCommand.CanExecute(text))
            ViewModel.SavePageRangeCommand.Execute(text);
    }

    // ═══ Theme toggle ═══

    private bool _isDark = true; // dark by default

    private void OnThemeToggle(object? sender, RoutedEventArgs e)
    {
        _isDark = !_isDark;
        Application.Current!.RequestedThemeVariant = _isDark ? ThemeVariant.Dark : ThemeVariant.Light;

        ThemeIcon.Data = IconService.CreateGeometry(_isDark ? Iconr.Icon.sun : Iconr.Icon.moon);
    }
}
