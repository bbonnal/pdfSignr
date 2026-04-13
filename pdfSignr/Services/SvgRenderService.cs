using SkiaSharp;
using Svg.Skia;

namespace pdfSignr.Services;

public static class SvgRenderService
{
    private const double SvgDpiToPoints = 72.0 / 96.0; // SVG uses 96 DPI, PDF uses 72

    public static (double WidthPt, double HeightPt) GetSvgSize(string svgPath)
    {
        using var svg = new SKSvg();
        svg.Load(svgPath);
        if (svg.Picture == null) return (0, 0);

        var bounds = svg.Picture.CullRect;
        return (bounds.Width * SvgDpiToPoints, bounds.Height * SvgDpiToPoints);
    }

    public static Avalonia.Media.Imaging.Bitmap RenderForDisplay(string svgPath, double scale, int renderDpi)
    {
        var (pixelW, pixelH, svg, bounds) = PrepareRender(svgPath, scale, renderDpi);
        using var _ = svg;
        var picture = svg.Picture!;

        using var surface = SKSurface.Create(new SKImageInfo(pixelW, pixelH, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(pixelW / bounds.Width, pixelH / bounds.Height);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(picture);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray();
        using var stream = new MemoryStream(bytes);
        return new Avalonia.Media.Imaging.Bitmap(stream);
    }

    public static byte[] RenderToPng(string svgPath, double scale, int dpi = 300)
    {
        var (pixelW, pixelH, svg, bounds) = PrepareRender(svgPath, scale, dpi);
        using var _ = svg;
        var picture = svg.Picture!;

        using var surface = SKSurface.Create(new SKImageInfo(pixelW, pixelH, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(pixelW / bounds.Width, pixelH / bounds.Height);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(picture);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static (int PixelW, int PixelH, SKSvg Svg, SKRect Bounds) PrepareRender(
        string svgPath, double scale, int dpi)
    {
        var svg = new SKSvg();
        svg.Load(svgPath);
        if (svg.Picture == null) throw new InvalidOperationException("Failed to load SVG");

        var bounds = svg.Picture.CullRect;
        double widthPt = bounds.Width * SvgDpiToPoints * scale;
        double heightPt = bounds.Height * SvgDpiToPoints * scale;
        int pixelW = Math.Max(1, (int)(widthPt / 72.0 * dpi));
        int pixelH = Math.Max(1, (int)(heightPt / 72.0 * dpi));

        return (pixelW, pixelH, svg, bounds);
    }
}
