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

    // Bounds concurrent pdfium calls to leave headroom for the UI thread.
    private readonly SemaphoreSlim _renderGate = new(Math.Max(1, Environment.ProcessorCount / 2));

    // Page offset cache for O(log n) visibility lookup (list mode only).
    // Invalidated by structure/rotation/zoom/grid changes.
    private double[] _pageTops = [];
    private double[] _pageHeights = [];
    private bool _pageOffsetCacheValid;

    // Fixed arrow column width in layout units (scales with zoom like the page)
    private const double ArrowColumnLayoutWidth = 34;

    private double NextZoomIn() => System.Math.Clamp(_zoom * ZoomFactor, MinZoom, MaxZoom);
    private double NextZoomOut() => System.Math.Clamp(_zoom / ZoomFactor, MinZoom, MaxZoom);

    private void OnScrollWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        double newZoom = e.Delta.Y > 0 ? NextZoomIn() : NextZoomOut();
        ApplyZoom(newZoom, e.GetPosition(PdfScrollViewer));
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
        ApplyZoom(System.Math.Clamp(available / totalLayoutWidth, MinZoom, MaxZoom), viewportCenter);
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
        ApplyZoom(System.Math.Clamp(available / pageScreenHeight, MinZoom, MaxZoom), viewportCenter);

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
        // Concurrency is bounded by _renderGate to avoid overwhelming pdfium / CPU.
        // Continuations run on the UI thread so bitmap swaps are safe.
        var renderTasks = pagesToRender.Select(async item =>
        {
            var (page, pdfBytes, srcIdx, rotDeg, password) = item;
            try
            {
                await _renderGate.WaitAsync(cts.Token);
                Avalonia.Media.Imaging.Bitmap bitmap;
                try
                {
                    bitmap = await Task.Run(
                        () => renderService.RenderPage(pdfBytes, srcIdx, dpi, rotDeg, password),
                        cts.Token);
                }
                finally
                {
                    _renderGate.Release();
                }

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
        _backgroundLoadCts?.Cancel();
        _pageDpi.Clear();
        _targetDpi = PdfConstants.RenderDpi;

        // Pages arrive with null bitmaps — render visible pages first, then the rest
        Dispatcher.UIThread.Post(async () =>
        {
            FitToWidth();
            UpdateCurrentPageIndicator();
            await RerenderVisibleAsync();
            try { await RenderRemainingPagesAsync(); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Background render failed: {ex}");
            }
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

        int dop = Math.Max(1, Environment.ProcessorCount / 2);
        try
        {
            await Parallel.ForEachAsync(
                unrendered,
                new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = cts.Token },
                async (page, innerCt) =>
                {
                    if (_pageDpi.ContainsKey(page)) return;
                    await _renderGate.WaitAsync(innerCt);
                    Avalonia.Media.Imaging.Bitmap bitmap;
                    try
                    {
                        bitmap = await Task.Run(
                            () => renderService.RenderPage(
                                page.Source.PdfBytes, page.Source.SourcePageIndex, dpi, page.RotationDegrees, page.Source.Password),
                            innerCt);
                    }
                    finally
                    {
                        _renderGate.Release();
                    }

                    if (innerCt.IsCancellationRequested) { bitmap.Dispose(); return; }

                    // Bitmap swap must happen on the UI thread.
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (innerCt.IsCancellationRequested) { bitmap.Dispose(); return; }
                        page.ReplaceBitmap(bitmap);
                        _pageDpi[page] = dpi;
                    });
                });
        }
        catch (OperationCanceledException) { }
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
        int dpi = _targetDpi;
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
}
