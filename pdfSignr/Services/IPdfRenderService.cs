using Avalonia.Media.Imaging;

namespace pdfSignr.Services;

public interface IPdfRenderService
{
    int GetPageCount(byte[] pdfBytes, string? password = null);
    (double WidthPt, double HeightPt) GetPageSize(byte[] pdfBytes, int pageIndex, string? password = null);
    IReadOnlyList<(double WidthPt, double HeightPt)> GetAllPageSizes(byte[] pdfBytes, string? password = null);
    Bitmap RenderPage(byte[] pdfBytes, int pageIndex, int dpi, int rotationDegrees = 0, string? password = null);

    /// <summary>
    /// Renders several pages from the same document in one shot, opening the PDF
    /// only once. Yields bitmaps in the same order as <paramref name="pageIndices"/>.
    /// Callers own the returned bitmaps.
    /// </summary>
    IEnumerable<Bitmap> RenderPages(
        byte[] pdfBytes, IReadOnlyList<int> pageIndices, int dpi,
        int rotationDegrees = 0, string? password = null)
    {
        foreach (var idx in pageIndices)
            yield return RenderPage(pdfBytes, idx, dpi, rotationDegrees, password);
    }
}
