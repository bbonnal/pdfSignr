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

    /// <summary>Maps a raw DPI to a fixed ladder so smooth zoom doesn't re-render every frame.</summary>
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

    /// <summary>Caps DPI so a single bitmap stays under <see cref="MaxBitmapPixels"/>
    /// (pixels = widthPt·heightPt·dpi²/72²).</summary>
    public static int ClampDpiForPage(int requestedDpi, double widthPt, double heightPt)
    {
        double area = widthPt * heightPt;
        if (area <= 0) return requestedDpi;
        double maxDpi = Math.Sqrt(MaxBitmapPixels * 72.0 * 72.0 / area);
        int clamped = (int)Math.Min(requestedDpi, maxDpi);
        return Math.Max(72, clamped);
    }

    /// <summary>Page indices intersecting the viewport. Tops must be ascending and the
    /// two arrays the same length.</summary>
    public static HashSet<int> VisibleIndices(
        double[] pageTops, double[] pageHeights, double scrollY, double viewportHeight)
    {
        var visible = new HashSet<int>();
        int pageCount = pageTops.Length;
        if (pageCount == 0 || pageHeights.Length != pageCount) return visible;

        double topEdge = scrollY;
        double bottomEdge = scrollY + viewportHeight;

        int lo = 0, hi = pageCount;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (pageTops[mid] < topEdge) lo = mid + 1;
            else hi = mid;
        }

        for (int i = Math.Max(0, lo - 1); i < pageCount; i++)
        {
            if (pageTops[i] > bottomEdge) break;
            if (pageTops[i] + pageHeights[i] < topEdge) continue;
            visible.Add(i);
        }
        return visible;
    }

    public static (int Min, int Max) ExpandRing(int visibleMin, int visibleMax, int radius, int pageCount)
    {
        int min = Math.Max(0, visibleMin - radius);
        int max = Math.Min(pageCount - 1, visibleMax + radius);
        return (min, max);
    }
}
