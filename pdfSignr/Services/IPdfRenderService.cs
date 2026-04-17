using Avalonia.Media.Imaging;

namespace pdfSignr.Services;

public interface IPdfRenderService
{
    int GetPageCount(byte[] pdfBytes, string? password = null);
    (double WidthPt, double HeightPt) GetPageSize(byte[] pdfBytes, int pageIndex, string? password = null);
    IReadOnlyList<(double WidthPt, double HeightPt)> GetAllPageSizes(byte[] pdfBytes, string? password = null);
    Bitmap RenderPage(byte[] pdfBytes, int pageIndex, int dpi, int rotationDegrees = 0, string? password = null);
}
