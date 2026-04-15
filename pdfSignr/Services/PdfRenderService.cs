using System.Diagnostics.CodeAnalysis;
using PDFtoImage;

namespace pdfSignr.Services;

[SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
public static class PdfRenderService
{
    public static int GetPageCount(byte[] pdfBytes)
        => Conversion.GetPageCount(pdfBytes);

    public static (double WidthPt, double HeightPt) GetPageSize(byte[] pdfBytes, int pageIndex)
    {
        var size = Conversion.GetPageSize(pdfBytes, page: pageIndex);
        return (size.Width, size.Height);
    }

    public static Avalonia.Media.Imaging.Bitmap RenderPage(byte[] pdfBytes, int pageIndex, int dpi)
    {
        using var skBitmap = Conversion.ToImage(pdfBytes, page: pageIndex, options: new(Dpi: dpi));
        return BitmapConvert.ToAvaloniaBitmap(skBitmap);
    }
}
