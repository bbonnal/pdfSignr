using pdfSignr.Models;
using SkiaSharp;
using Svg.Skia;

namespace pdfSignr.Services;

public static class SvgRenderService
{

    public static (double WidthPt, double HeightPt) GetSvgSize(string svgPath)
    {
        using var svg = new SKSvg();
        svg.Load(svgPath);
        if (svg.Picture == null) return (0, 0);

        var bounds = svg.Picture.CullRect;
        return (bounds.Width * PdfConstants.SvgDpiToPoints, bounds.Height * PdfConstants.SvgDpiToPoints);
    }

    public static Avalonia.Media.Imaging.Bitmap RenderForDisplay(string svgPath, double scale, int renderDpi)
    {
        var (pixelW, pixelH, svg, bounds) = PrepareRender(svgPath, scale, renderDpi);
        using var _ = svg;
        return RenderSvgToBitmap(svg, bounds, pixelW, pixelH);
    }

    /// <summary>Returns SVG size and rendered bitmap in a single parse (avoids loading the SVG twice).</summary>
    public static (double WidthPt, double HeightPt, Avalonia.Media.Imaging.Bitmap Bitmap)
        GetSizeAndRenderForDisplay(string svgPath, double scale, int renderDpi)
    {
        var (pixelW, pixelH, svg, bounds) = PrepareRender(svgPath, scale, renderDpi);
        using var _ = svg;

        double widthPt = bounds.Width * PdfConstants.SvgDpiToPoints * scale;
        double heightPt = bounds.Height * PdfConstants.SvgDpiToPoints * scale;
        var bitmap = RenderSvgToBitmap(svg, bounds, pixelW, pixelH);

        return (widthPt, heightPt, bitmap);
    }

    public static byte[] RenderToVectorPdf(string svgPath, double scale)
    {
        using var svg = new SKSvg();
        svg.Load(svgPath);
        if (svg.Picture == null) return [];

        var bounds = svg.Picture.CullRect;
        float pageW = (float)(bounds.Width * PdfConstants.SvgDpiToPoints * scale);
        float pageH = (float)(bounds.Height * PdfConstants.SvgDpiToPoints * scale);

        using var stream = new MemoryStream();
        using (var doc = SKDocument.CreatePdf(stream))
        {
            using var canvas = doc.BeginPage(pageW, pageH);
            canvas.Scale(pageW / bounds.Width, pageH / bounds.Height);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(svg.Picture);
            doc.EndPage();
            doc.Close();
        }
        return stream.ToArray();
    }

    public static (double WidthPt, double HeightPt) GetImageSize(string path)
    {
        using var codec = SKCodec.Create(path);
        if (codec == null) return (0, 0);
        return (codec.Info.Width * PdfConstants.SvgDpiToPoints, codec.Info.Height * PdfConstants.SvgDpiToPoints);
    }

    public static Avalonia.Media.Imaging.Bitmap ResampleForDisplay(string path, double widthPt, double heightPt, int dpi)
    {
        int pixelW = Math.Max(1, (int)(widthPt / PdfConstants.PointsPerInch * dpi));
        int pixelH = Math.Max(1, (int)(heightPt / PdfConstants.PointsPerInch * dpi));

        using var original = SKBitmap.Decode(path);
        if (original == null) return new Avalonia.Media.Imaging.Bitmap(path);

        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
        using var resized = original.Resize(new SKSizeI(pixelW, pixelH), sampling);
        return BitmapConvert.ToAvaloniaBitmap(resized ?? original);
    }

    private static Avalonia.Media.Imaging.Bitmap RenderSvgToBitmap(SKSvg svg, SKRect bounds, int pixelW, int pixelH)
    {
        using var surface = SKSurface.Create(new SKImageInfo(pixelW, pixelH, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(pixelW / bounds.Width, pixelH / bounds.Height);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(svg.Picture!);

        using var image = surface.Snapshot();
        using var skBitmap = SKBitmap.FromImage(image);
        return BitmapConvert.ToAvaloniaBitmap(skBitmap);
    }

    private static (int PixelW, int PixelH, SKSvg Svg, SKRect Bounds) PrepareRender(
        string svgPath, double scale, int dpi)
    {
        var svg = new SKSvg();
        svg.Load(svgPath);
        if (svg.Picture == null) throw new InvalidOperationException("Failed to load SVG");

        var bounds = svg.Picture.CullRect;
        double widthPt = bounds.Width * PdfConstants.SvgDpiToPoints * scale;
        double heightPt = bounds.Height * PdfConstants.SvgDpiToPoints * scale;
        int pixelW = Math.Max(1, (int)(widthPt / PdfConstants.PointsPerInch * dpi));
        int pixelH = Math.Max(1, (int)(heightPt / PdfConstants.PointsPerInch * dpi));

        return (pixelW, pixelH, svg, bounds);
    }
}
