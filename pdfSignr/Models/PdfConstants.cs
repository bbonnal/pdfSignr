namespace pdfSignr.Models;

public static class PdfConstants
{
    public const int RenderDpi = 150;
    public const double PointsPerInch = 72.0;
    public const double SvgDpi = 96.0;
    public const double DpiScale = RenderDpi / PointsPerInch;
    public const double SvgDpiToPoints = PointsPerInch / SvgDpi;
}
