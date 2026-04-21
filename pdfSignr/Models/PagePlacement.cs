namespace pdfSignr.Models;

internal static class PagePlacement
{
    /// <summary>
    /// Clamps an annotation's top-left (x, y) so its (w, h) bounding box stays fully
    /// inside a page of (pageW, pageH). Pages smaller than the annotation pin to 0.
    /// </summary>
    public static (double X, double Y) ClampToPage(
        double x, double y, double w, double h, double pageW, double pageH)
    {
        return (
            Math.Clamp(x, 0, Math.Max(0, pageW - w)),
            Math.Clamp(y, 0, Math.Max(0, pageH - h)));
    }
}
