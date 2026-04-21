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

    // Bounds concurrent pdfium calls to leave headroom for the UI thread.
    private readonly SemaphoreSlim _renderGate = new(Math.Max(1, Environment.ProcessorCount / 2));

    // Keep pages near the viewport rendered; evict beyond the retention ring so
    // memory stays O(window), not O(document).
    private const int PrefetchRingRadius = 3;
    private const int RetentionRingRadius = 12;

    // Cap bitmap pixel count to prevent OOM on extreme zoom (4 bytes/pixel → 256 MB max).
    private const long MaxBitmapPixels = 64L * 1024 * 1024;

    // Page offset cache for O(log n) visibility lookup (list mode only).
    // Invalidated by structure/rotation/zoom/grid changes.
    private double[] _pageTops = [];
    private double[] _pageHeights = [];
    private bool _pageOffsetCacheValid;

    // Fixed arrow column width in layout units (scales with zoom like the page)
    private const double ArrowColumnLayoutWidth = 34;

    private double NextZoomIn() => _zoom * ZoomFactor;
    private double NextZoomOut() => _zoom / ZoomFactor;

    private void OnScrollWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        double newZoom = e.Delta.Y > 0 ? NextZoomIn() : NextZoomOut();
        ApplyZoom(newZoom, e.GetPosition(PdfScrollViewer));
        e.Handled = true;
    }

    // ApplyZoom is the single zoom-clamp authority — callers pass raw values.
    private void ApplyZoom(double level, Point? anchor = null)
    {
        double oldZoom = _zoom;
        _zoom = System.Math.Clamp(level, MinZoom, MaxZoom);

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
        _pageOffsetCacheValid = false;
        ViewModel.Viewport.ButtonScale = 1.0 / _zoom;
        ViewModel.Viewport.SelectionBorderThickness = new Thickness(3.0 / _zoom);
        ViewModel.Viewport.HitBorderThickness = new Thickness(28.0 / _zoom);
        ViewModel.Viewport.HitBorderMargin = new Thickness(-28.0 / _zoom);
        ViewModel.Viewport.ZoomPercent = (int)System.Math.Round(_zoom * 100);
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
        ApplyZoom(available / totalLayoutWidth, viewportCenter);
    }

    public void FitToHeight()
    {
        if (ViewModel.Pages.Count == 0) return;

        double available = PdfScrollViewer.Bounds.Height - FitToWidthPadding;
        if (available <= 0) return;

        // Fit the tallest page to the viewport height
        double pageScreenHeight = ViewModel.Pages.Max(p => p.HeightPt) * PdfConstants.DpiScale;
        if (pageScreenHeight <= 0) return;

        // Remember which page we're on so we can snap its top to the viewport after zoom
        var visible = GetVisiblePageIndices();
        int anchorIndex = visible.Count > 0 ? visible.Min() : 0;

        var viewportCenter = new Point(
            PdfScrollViewer.Viewport.Width / 2,
            PdfScrollViewer.Viewport.Height / 2);
        ApplyZoom(available / pageScreenHeight, viewportCenter);

        // Snap to the anchor page's top so a full page is visible
        ZoomTransform.UpdateLayout();
        if (TryRebuildPageOffsetCache(ViewModel.Pages.Count)
            && anchorIndex >= 0 && anchorIndex < _pageTops.Length)
        {
            PdfScrollViewer.Offset = new Vector(
                PdfScrollViewer.Offset.X,
                Math.Max(0, _pageTops[anchorIndex]));
        }
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
        UpdateCurrentPageIndicator();
        RestartDebounce(ref _scrollTimer, TimeSpan.FromMilliseconds(200),
            () => _ = RerenderVisibleAsync());
    }

    private void UpdateCurrentPageIndicator()
    {
        var visible = GetVisiblePageIndices();
        if (visible.Count > 0)
        {
            ViewModel.Viewport.CurrentPageInView = visible.Min() + 1;
            ViewModel.UpdateStatusText();
        }
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
        int pageCount = ViewModel.Pages.Count;
        if (pageCount == 0) return visible;

        double viewportH = PdfScrollViewer.Viewport.Height;

        // Grid mode has 2D layout: binary search on vertical tops doesn't capture all
        // visible items in a row. Fall back to container walk.
        if (ViewModel.Viewport.IsGridMode)
            return GetVisiblePageIndicesByContainers(viewportH);

        // List mode: build cache once, then O(log n) lookup.
        if (!_pageOffsetCacheValid || _pageTops.Length != pageCount)
        {
            if (!TryRebuildPageOffsetCache(pageCount))
                return GetVisiblePageIndicesByContainers(viewportH);
        }

        double scrollY = PdfScrollViewer.Offset.Y;
        double topEdge = scrollY;
        double bottomEdge = scrollY + viewportH;

        // Binary search: find first page with top >= scrollY; visible range starts at max(0, idx-1).
        int lo = 0, hi = pageCount;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_pageTops[mid] < topEdge) lo = mid + 1;
            else hi = mid;
        }
        int start = Math.Max(0, lo - 1);

        for (int i = start; i < pageCount; i++)
        {
            if (_pageTops[i] > bottomEdge) break;
            if (_pageTops[i] + _pageHeights[i] < topEdge) continue;
            visible.Add(i);
        }
        return visible;
    }

    private HashSet<int> GetVisiblePageIndicesByContainers(double viewportH)
    {
        var visible = new HashSet<int>();
        var itemsControl = (ItemsControl)ZoomTransform.Child!;
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
                break;
            }
        }
        return visible;
    }

    // Computes scroll-content-relative top and height for every page from container bounds.
    // Returns false if any container is missing or un-laid-out (caller falls back to container walk).
    private bool TryRebuildPageOffsetCache(int pageCount)
    {
        var itemsControl = (ItemsControl)ZoomTransform.Child!;
        double scrollY = PdfScrollViewer.Offset.Y;

        if (_pageTops.Length != pageCount)
        {
            _pageTops = new double[pageCount];
            _pageHeights = new double[pageCount];
        }

        for (int i = 0; i < pageCount; i++)
        {
            var container = itemsControl.ContainerFromIndex(i);
            if (container == null) return false;
            var top = container.TranslatePoint(new Point(0, 0), PdfScrollViewer);
            if (top == null) return false;
            _pageTops[i] = top.Value.Y + scrollY;
            _pageHeights[i] = container.Bounds.Height;
        }

        _pageOffsetCacheValid = true;
        return true;
    }

    // Clamp DPI so a single rendered bitmap stays under MaxBitmapPixels.
    // pixels = widthPt*heightPt * dpi² / 72² → dpi_max = sqrt(MaxPixels * 72² / area)
    private static int ClampDpiForPage(int requestedDpi, double widthPt, double heightPt)
    {
        double area = widthPt * heightPt;
        if (area <= 0) return requestedDpi;
        double maxDpi = Math.Sqrt(MaxBitmapPixels * 72.0 * 72.0 / area);
        int clamped = (int)Math.Min(requestedDpi, maxDpi);
        return Math.Max(72, clamped);
    }

    private async Task RerenderVisibleAsync()
    {
        _rerenderCts?.Cancel();
        _rerenderCts?.Dispose();
        var cts = new CancellationTokenSource();
        _rerenderCts = cts;

        var pages = ViewModel.Pages;
        int pageCount = pages.Count;
        if (pageCount == 0) return;

        var visible = GetVisiblePageIndices();
        int dpi = _targetDpi;
        var renderService = ViewModel.RenderService;

        // Expand visible set into prefetch (render) and retention (keep-alive) rings.
        int minVisible = visible.Count > 0 ? visible.Min() : 0;
        int maxVisible = visible.Count > 0 ? visible.Max() : 0;
        int prefetchMin = Math.Max(0, minVisible - PrefetchRingRadius);
        int prefetchMax = Math.Min(pageCount - 1, maxVisible + PrefetchRingRadius);
        int retentionMin = Math.Max(0, minVisible - RetentionRingRadius);
        int retentionMax = Math.Min(pageCount - 1, maxVisible + RetentionRingRadius);

        // Evict bitmaps outside the retention ring so memory stays bounded.
        for (int i = 0; i < pageCount; i++)
        {
            if (i >= retentionMin && i <= retentionMax) continue;
            var page = pages[i];
            if (_pageDpi.TryRemove(page, out _))
                page.ReplaceBitmap(null);
        }

        // Decide which pages need a render. Apply a per-page DPI cap so a
        // single huge page at extreme zoom can't OOM.
        var pagesToRender = new List<(PageItem Page, int EffectiveDpi)>();
        for (int i = prefetchMin; i <= prefetchMax; i++)
        {
            var page = pages[i];
            int effDpi = ClampDpiForPage(dpi, page.WidthPt, page.HeightPt);
            if (!_pageDpi.TryGetValue(page, out var cur) || cur != effDpi)
                pagesToRender.Add((page, effDpi));
        }

        // Group by (dpi, rotation) so each group can render with one pdfium open,
        // amortizing the PDF parse cost across the batch (the big win on huge files).
        var groups = pagesToRender
            .GroupBy(t => (t.EffectiveDpi, t.Page.RotationDegrees))
            .ToList();

        foreach (var group in groups)
        {
            if (cts.Token.IsCancellationRequested) break;
            var (effDpi, rotDeg) = group.Key;
            var groupPages = group.Select(t => t.Page).ToList();
            var groupIndices = groupPages.Select(p => p.Source.SourcePageIndex).ToList();
            var pdfBytes = groupPages[0].Source.PdfBytes;
            var password = groupPages[0].Source.Password;

            try
            {
                await _renderGate.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) { return; }

            List<Avalonia.Media.Imaging.Bitmap>? bitmaps = null;
            try
            {
                bitmaps = await Task.Run(() =>
                {
                    var list = new List<Avalonia.Media.Imaging.Bitmap>(groupIndices.Count);
                    foreach (var bmp in renderService.RenderPages(pdfBytes, groupIndices, effDpi, rotDeg, password))
                    {
                        if (cts.Token.IsCancellationRequested) { bmp.Dispose(); break; }
                        list.Add(bmp);
                    }
                    return list;
                }, cts.Token);
            }
            catch (OperationCanceledException) { return; }
            finally
            {
                _renderGate.Release();
            }

            if (cts.Token.IsCancellationRequested)
            {
                foreach (var b in bitmaps!) b.Dispose();
                return;
            }

            for (int i = 0; i < bitmaps.Count && i < groupPages.Count; i++)
            {
                var page = groupPages[i];
                page.ReplaceBitmap(bitmaps[i]);
                _pageDpi[page] = effDpi;
            }
        }

        if (cts.Token.IsCancellationRequested) return;

        // Re-render annotation bitmaps on visible pages at the target DPI
        var annotations = pages
            .Where(p => visible.Contains(p.Index))
            .SelectMany(p => p.Annotations)
            .OfType<SvgAnnotation>()
            .Where(a => a.RenderedBitmap != null && a.RenderedDpi != dpi)
            .ToList();

        var svgRenderer = ViewModel.SvgRenderer;
        var annTasks = annotations.Select(async ann =>
        {
            try
            {
                if (cts.Token.IsCancellationRequested) return;
                await _renderGate.WaitAsync(cts.Token);
                Avalonia.Media.Imaging.Bitmap bitmap;
                try
                {
                    bitmap = await Task.Run(() => svgRenderer.RenderForAnnotation(ann, dpi), cts.Token);
                }
                finally
                {
                    _renderGate.Release();
                }
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
        _pageDpi.Clear();
        _targetDpi = PdfConstants.RenderDpi;

        Dispatcher.UIThread.Post(async () =>
        {
            FitToWidth();
            UpdateCurrentPageIndicator();
            try { await RerenderVisibleAsync(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Initial render failed: {ex}");
            }
        }, DispatcherPriority.Background);
    }

    private void OnPageStructureChanged()
    {
        // Remove entries for pages no longer in the document
        var current = new HashSet<PageItem>(ViewModel.Pages);
        foreach (var key in _pageDpi.Keys.Where(k => !current.Contains(k)).ToList())
            _pageDpi.TryRemove(key, out _);
        _pageOffsetCacheValid = false;
    }

    private async void OnPageRotated(PageItem page)
    {
        int dpi = ClampDpiForPage(_targetDpi, page.WidthPt, page.HeightPt);
        var renderService = ViewModel.RenderService;
        _pageOffsetCacheValid = false;
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
        ViewModel.Viewport.IsGridMode = !ViewModel.Viewport.IsGridMode;
        ApplyGridMode(ViewModel.Viewport.IsGridMode);
    }

    public void ApplyGridMode(bool gridMode)
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

        // Grid switch invalidates the page-offset cache (different layout + heights).
        _pageOffsetCacheValid = false;

        // Visible pages change when layout switches, schedule re-render
        ScheduleRerender();
    }

    // ═══ Zoom buttons ═══

    private void OnFitToWidth(object? sender, RoutedEventArgs e) => FitToWidth();
    private void OnFitToHeight(object? sender, RoutedEventArgs e) => FitToHeight();
    private void OnZoomIn(object? sender, RoutedEventArgs e) => ApplyZoom(NextZoomIn());
    private void OnZoomOut(object? sender, RoutedEventArgs e) => ApplyZoom(NextZoomOut());

    // ═══ Page navigation ═══

    internal void ScrollToPage(int pageIndexZeroBased)
    {
        int pageCount = ViewModel.Pages.Count;
        if (pageCount == 0) return;
        int idx = Math.Clamp(pageIndexZeroBased, 0, pageCount - 1);

        ZoomTransform.UpdateLayout();
        if (!TryRebuildPageOffsetCache(pageCount)) return;

        PdfScrollViewer.Offset = new Vector(
            PdfScrollViewer.Offset.X,
            Math.Max(0, _pageTops[idx]));
    }

    internal bool TryNavigateToPage(string? text)
    {
        if (!int.TryParse(text?.Trim(), out int oneBased)) return false;
        int pageCount = ViewModel.Pages.Count;
        if (pageCount == 0) return false;
        int idx = Math.Clamp(oneBased, 1, pageCount) - 1;
        ScrollToPage(idx);
        return true;
    }
}
