using Avalonia.Media.Imaging;
using pdfSignr.Models;

namespace pdfSignr.Services;

public interface ISvgRenderService
{
    (double WidthPt, double HeightPt) GetSvgSize(string svgPath);
    (double WidthPt, double HeightPt) GetImageSize(string path);

    Bitmap RenderForDisplay(string svgPath, double scale, int renderDpi);
    (double WidthPt, double HeightPt, Bitmap Bitmap) GetSizeAndRenderForDisplay(
        string svgPath, double scale, int renderDpi);
    Bitmap ResampleForDisplay(string path, double widthPt, double heightPt, int dpi);

    byte[] RenderToVectorPdf(string svgPath, double scale);

    Bitmap RenderForAnnotation(SvgAnnotation annotation, int dpi);
}
