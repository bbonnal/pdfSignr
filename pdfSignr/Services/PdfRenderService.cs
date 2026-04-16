using System.Diagnostics.CodeAnalysis;
using PDFtoImage;

namespace pdfSignr.Services;

[SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
/// <summary>Renders PDF pages to Avalonia bitmaps via pdfium (PDFtoImage).</summary>
public static class PdfRenderService
{
    /// <summary>Returns the number of pages in a PDF document.</summary>
    public static int GetPageCount(byte[] pdfBytes)
        => Conversion.GetPageCount(pdfBytes);

    /// <summary>Returns the page dimensions in PDF points (1/72 inch).</summary>
    public static (double WidthPt, double HeightPt) GetPageSize(byte[] pdfBytes, int pageIndex)
    {
        var size = Conversion.GetPageSize(pdfBytes, page: pageIndex);
        return (size.Width, size.Height);
    }

    /// <summary>Returns all page dimensions in a single document open (much faster than per-page calls for large files).</summary>
    public static IReadOnlyList<(double WidthPt, double HeightPt)> GetAllPageSizes(byte[] pdfBytes)
        => Conversion.GetPageSizes(pdfBytes)
            .Select(s => ((double)s.Width, (double)s.Height))
            .ToList();

    /// <summary>Renders a single page to an Avalonia bitmap at the given DPI, with optional rotation.</summary>
    public static Avalonia.Media.Imaging.Bitmap RenderPage(byte[] pdfBytes, int pageIndex, int dpi, int rotationDegrees = 0)
    {
        var rotation = rotationDegrees switch
        {
            90  => PdfRotation.Rotate90,
            180 => PdfRotation.Rotate180,
            270 => PdfRotation.Rotate270,
            _   => PdfRotation.Rotate0
        };
        using var skBitmap = Conversion.ToImage(pdfBytes, page: pageIndex, options: new(Dpi: dpi, Rotation: rotation));
        return BitmapConvert.ToAvaloniaBitmap(skBitmap);
    }
}
