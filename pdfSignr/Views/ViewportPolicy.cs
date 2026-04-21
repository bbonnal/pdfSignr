namespace pdfSignr.Views;

/// <summary>
/// Pure policy functions used by the viewport's adaptive-DPI renderer and visibility lookup.
/// Extracted from MainWindow so the numerical behavior can be exercised without an Avalonia
/// visual tree. All methods are deterministic and allocation-light.
/// </summary>
internal static class ViewportPolicy
{
    /// <summary>Pages inside this ring around the visible set are rendered at the new DPI.</summary>
    public const int PrefetchRingRadius = 3;

    /// <summary>Pages outside this ring are evicted so memory stays O(window), not O(document).</summary>
    public const int RetentionRingRadius = 12;

    /// <summary>Max pixels a single rendered bitmap may occupy (4 bytes/pixel → 256 MB cap).</summary>
    public const long MaxBitmapPixels = 64L * 1024 * 1024;

    /// <summary>
    /// Snaps a requested DPI to a fixed ladder. Reduces re-render frequency during smooth
    /// zoom while keeping vector PDFs sharp — no hard ceiling.
    /// </summary>
    public static int QuantizeDpi(int dpi)
    {
        if (dpi <= 100) return 96;
        if (dpi <= 150) return 150;
        if (dpi <= 225) return 200;
        if (dpi <= 350) return 300;
        if (dpi <= 500) return 400;
        if (dpi <= 700) return 600;
        if (dpi <= 1000) return 800;
        return 1200;
    }

    /// <summary>
    /// Caps the effective DPI so a single-page bitmap stays under <see cref="MaxBitmapPixels"/>.
    /// Derived from pixels = widthPt * heightPt * dpi² / 72².
    /// </summary>
    public static int ClampDpiForPage(int requestedDpi, double widthPt, double heightPt)
    {
        double area = widthPt * heightPt;
        if (area <= 0) return requestedDpi;
        double maxDpi = Math.Sqrt(MaxBitmapPixels * 72.0 * 72.0 / area);
        int clamped = (int)Math.Min(requestedDpi, maxDpi);
        return Math.Max(72, clamped);
    }

    /// <summary>
    /// Returns the page indices intersecting the viewport, given per-page tops/heights in
    /// scroll-content space. Uses a binary search to find the first candidate, then linearly
    /// scans until past the viewport. Inputs must be same length; tops must be sorted ascending.
    /// </summary>
    public static IEnumerable<int> VisibleIndices(
        double[] pageTops, double[] pageHeights, double scrollY, double viewportHeight)
    {
        int pageCount = pageTops.Length;
        if (pageCount == 0 || pageHeights.Length != pageCount)
            yield break;

        double topEdge = scrollY;
        double bottomEdge = scrollY + viewportHeight;

        int lo = 0, hi = pageCount;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (pageTops[mid] < topEdge) lo = mid + 1;
            else hi = mid;
        }
        int start = Math.Max(0, lo - 1);

        for (int i = start; i < pageCount; i++)
        {
            if (pageTops[i] > bottomEdge) break;
            if (pageTops[i] + pageHeights[i] < topEdge) continue;
            yield return i;
        }
    }

    /// <summary>Expands a visible range by a ring radius, clamped to the page-count domain.</summary>
    public static (int Min, int Max) ExpandRing(int visibleMin, int visibleMax, int radius, int pageCount)
    {
        int min = Math.Max(0, visibleMin - radius);
        int max = Math.Min(pageCount - 1, visibleMax + radius);
        return (min, max);
    }
}
