using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Iconr;
using pdfSignr.Models;
using pdfSignr.ViewModels;

namespace pdfSignr.Views;

// Zoom, adaptive DPI rendering, grid/list mode, scroll-triggered re-renders
public partial class MainWindow
{
    private double _zoom = 1.0;
    private double _screenScaling = 1.0;
    private int _targetDpi = PdfConstants.RenderDpi;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<PageItem, int> _pageDpi = new();
    private DispatcherTimer? _rerenderTimer;
    private DispatcherTimer? _scrollTimer;
    private CancellationTokenSource? _rerenderCts;
    private CancellationTokenSource? _backgroundLoadCts;

    // Fixed arrow column width in layout units (scales with zoom like the page)
    private const double ArrowColumnLayoutWidth = 34;

    private void OnScrollWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        double step = e.Delta.Y > 0 ? ZoomStep : -ZoomStep;
        var cursorInViewport = e.GetPosition(PdfScrollViewer);
        ApplyZoom(System.Math.Clamp(_zoom + step, MinZoom, MaxZoom), cursorInViewport);
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

        ZoomTransform.LayoutTransform = new Avalonia.Media.ScaleTransform(_zoom, _zoom);
        ViewModel.ButtonScale = 1.0 / _zoom;
        ViewModel.SelectionBorderThickness = new Thickness(3.0 / _zoom);
        ViewModel.ZoomPercent = (int)System.Math.Round(_zoom * 100);
        ViewModel.UpdateStatusText();

        // Adjust scroll so content point stays under anchor
        if (anchor is not null)
        {
            // Force layout so ScrollViewer extent updates
            ZoomTransform.UpdateLayout();
            double newOffX = anchorContentX * _zoom - anchorX;
            double newOffY = anchorContentY * _zoom - anchorY;
            PdfScrollViewer.Offset = new Vector(
                System.Math.Max(0, newOffX),
                System.Math.Max(0, newOffY));
        }

        ScheduleRerender();
    }

    public void FitToWidth()
    {
        if (ViewModel.Pages.Count == 0) return;

        double available = PdfScrollViewer.Bounds.Width - FitToWidthPadding;
        if (available <= 0) return;

        // Total layout width = widest page + both arrow columns; all scale together with zoom
        double pageScreenWidth = ViewModel.Pages.Max(p => p.WidthPt) * PdfConstants.DpiScale;
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
        ApplyZoom(System.Math.Clamp(available / totalLayoutWidth, MinZoom, MaxZoom), viewportCenter);
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

    internal HashSet<int> GetVisiblePageIndices()
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
            else if (foundVisible && top.Value.Y > viewportH)
            {
                break; // all remaining pages are below viewport (works for both list and grid)
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

        // Capture render service on UI thread — ViewModel.RenderService accesses DataContext which is thread-affine
        var renderService = ViewModel.RenderService;

        // Re-render pages that are visible AND not already at the target DPI
        var pagesToRender = ViewModel.Pages
            .Where(p => visible.Contains(p.Index)
                     && (!_pageDpi.TryGetValue(p, out var cur) || cur != dpi))
            .Select(p => (Page: p, p.Source.PdfBytes, p.Source.SourcePageIndex, p.RotationDegrees, p.Source.Password))
            .ToList();

        // Render in parallel — each page is independent.
        // Continuations run on the UI thread so bitmap swaps are safe.
        var renderTasks = pagesToRender.Select(async item =>
        {
            var (page, pdfBytes, srcIdx, rotDeg, password) = item;
            try
            {
                var bitmap = await Task.Run(
                    () => renderService.RenderPage(pdfBytes, srcIdx, dpi, rotDeg, password),
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
                var bitmap = await Task.Run(() => ann.RenderBitmap(dpi), cts.Token);
                if (cts.Token.IsCancellationRequested) { bitmap.Dispose(); return; }
                ann.ReplaceRenderedBitmap(bitmap, dpi);
            }
            catch (OperationCanceledException) { }
        });

        try { await Task.WhenAll(annTasks); }
        catch (OperationCanceledException) { }
    }

    // ═══ PDF load rendering ═══

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
            try { await RenderRemainingPagesAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Background render failed: {ex.Message}"); }
        }, DispatcherPriority.Background);
    }

    private async Task RenderRemainingPagesAsync()
    {
        _backgroundLoadCts?.Cancel();
        _backgroundLoadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _backgroundLoadCts = cts;

        int dpi = _targetDpi;
        var renderService = ViewModel.RenderService;
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
                    () => renderService.RenderPage(
                        page.Source.PdfBytes, page.Source.SourcePageIndex, dpi, page.RotationDegrees, page.Source.Password),
                    cts.Token);

                if (cts.Token.IsCancellationRequested) { bitmap.Dispose(); return; }

                page.ReplaceBitmap(bitmap);
                _pageDpi[page] = dpi;
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private void OnPageStructureChanged()
    {
        // Remove entries for pages no longer in the document
        var current = new HashSet<PageItem>(ViewModel.Pages);
        foreach (var key in _pageDpi.Keys.Where(k => !current.Contains(k)).ToList())
            _pageDpi.TryRemove(key, out _);
    }

    private async void OnPageRotated(PageItem page)
    {
        int dpi = _targetDpi;
        var renderService = ViewModel.RenderService;
        try
        {
            var bitmap = await Task.Run(() =>
                renderService.RenderPage(
                    page.Source.PdfBytes, page.Source.SourcePageIndex,
                    dpi, page.RotationDegrees, page.Source.Password));
            page.ReplaceBitmap(bitmap);
            _pageDpi[page] = dpi;
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Rotation render failed: {ex.Message}";
        }
    }

    // ═══ Grid / list toggle ═══

    private void OnToggleGridMode(object? sender, RoutedEventArgs e)
    {
        ViewModel.IsGridMode = !ViewModel.IsGridMode;
        ApplyGridMode(ViewModel.IsGridMode);
    }

    internal void ApplyGridMode(bool gridMode)
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

    // ═══ Zoom buttons ═══

    private void OnFitToWidth(object? sender, RoutedEventArgs e) => FitToWidth();
    private void OnZoomIn(object? sender, RoutedEventArgs e) => ApplyZoom(System.Math.Clamp(_zoom + ZoomStep, MinZoom, MaxZoom));
    private void OnZoomOut(object? sender, RoutedEventArgs e) => ApplyZoom(System.Math.Clamp(_zoom - ZoomStep, MinZoom, MaxZoom));
}
