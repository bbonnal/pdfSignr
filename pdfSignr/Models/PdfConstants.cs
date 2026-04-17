using PDFtoImage;

namespace pdfSignr.Models;

public static class PdfConstants
{
    public const int RenderDpi = 150;
    public const double PointsPerInch = 72.0;
    public const double SvgDpi = 96.0;
    public const double DpiScale = RenderDpi / PointsPerInch;
    public const double SvgDpiToPoints = PointsPerInch / SvgDpi;

    public static PdfRotation ToPdfRotation(int degrees) => degrees switch
    {
        90  => PdfRotation.Rotate90,
        180 => PdfRotation.Rotate180,
        270 => PdfRotation.Rotate270,
        _   => PdfRotation.Rotate0
    };
}
