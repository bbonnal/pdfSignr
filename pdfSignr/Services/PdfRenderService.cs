using System.Diagnostics.CodeAnalysis;
using PDFtoImage;

namespace pdfSignr.Services;

[SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
/// <summary>Renders PDF pages to Avalonia bitmaps via pdfium (PDFtoImage).</summary>
public class PdfRenderService : IPdfRenderService
{
    /// <summary>Returns the number of pages in a PDF document.</summary>
    public int GetPageCount(byte[] pdfBytes, string? password = null)
        => Conversion.GetPageCount(pdfBytes, password);

    /// <summary>Returns the page dimensions in PDF points (1/72 inch).</summary>
    public (double WidthPt, double HeightPt) GetPageSize(byte[] pdfBytes, int pageIndex, string? password = null)
    {
        var size = Conversion.GetPageSize(pdfBytes, page: pageIndex, password: password);
        return (size.Width, size.Height);
    }

    /// <summary>Returns all page dimensions in a single document open (much faster than per-page calls for large files).</summary>
    public IReadOnlyList<(double WidthPt, double HeightPt)> GetAllPageSizes(byte[] pdfBytes, string? password = null)
        => Conversion.GetPageSizes(pdfBytes, password)
            .Select(s => ((double)s.Width, (double)s.Height))
            .ToList();

    /// <summary>Renders a single page to an Avalonia bitmap at the given DPI, with optional rotation.</summary>
    public Avalonia.Media.Imaging.Bitmap RenderPage(byte[] pdfBytes, int pageIndex, int dpi, int rotationDegrees = 0, string? password = null)
    {
        var rotation = Models.PdfConstants.ToPdfRotation(rotationDegrees);
        using var skBitmap = Conversion.ToImage(pdfBytes, page: pageIndex, password: password, options: new(Dpi: dpi, Rotation: rotation));
        return BitmapConvert.ToAvaloniaBitmap(skBitmap);
    }

    /// <summary>
    /// Renders multiple pages from the same document with a single pdfium open.
    /// Amortizes the PDF parse cost across the batch — critical for large files.
    /// </summary>
    public IEnumerable<Avalonia.Media.Imaging.Bitmap> RenderPages(
        byte[] pdfBytes, IReadOnlyList<int> pageIndices, int dpi,
        int rotationDegrees = 0, string? password = null)
    {
        if (pageIndices.Count == 0) yield break;
        var rotation = Models.PdfConstants.ToPdfRotation(rotationDegrees);
        var options = new RenderOptions(Dpi: dpi, Rotation: rotation);
        foreach (var skBitmap in Conversion.ToImages(pdfBytes, pageIndices, password, options))
        {
            try { yield return BitmapConvert.ToAvaloniaBitmap(skBitmap); }
            finally { skBitmap.Dispose(); }
        }
    }
}
