using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using pdfSignr.Models;

namespace pdfSignr.Services;

/// <summary>Saves annotated PDF pages to disk using PDFsharp, embedding text and image annotations.</summary>
public static class PdfSaveService
{
    /// <summary>Saves a single page with its annotations to a new PDF file.</summary>
    public static void SaveSinglePage(
        string outputPath,
        PageSource source,
        IEnumerable<Annotation> annotations)
    {
        Save(outputPath, [(source, annotations)]);
    }

    /// <summary>Saves multiple pages with annotations to a new PDF file.</summary>
    public static void Save(
        string outputPath,
        IEnumerable<(PageSource Source, IEnumerable<Annotation> Annotations)> pages)
    {
        var sourceDocCache = new Dictionary<byte[], PdfDocument>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        var fileCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var outputDoc = new PdfDocument();

        try
        {
            foreach (var (source, annotations) in pages)
            {
                if (!sourceDocCache.TryGetValue(source.PdfBytes, out var sourceDoc))
                {
                    sourceDoc = PdfReader.Open(
                        new MemoryStream(source.PdfBytes), PdfDocumentOpenMode.Import);
                    sourceDocCache[source.PdfBytes] = sourceDoc;
                }

                var importedPage = outputDoc.AddPage(sourceDoc.Pages[source.SourcePageIndex]);

                var annList = annotations.ToList();
                if (annList.Count > 0)
                {
                    using var gfx = XGraphics.FromPdfPage(importedPage, XGraphicsPdfPageOptions.Append);
                    foreach (var annotation in annList)
                    {
                        switch (annotation)
                        {
                            case TextAnnotation text:
                                DrawText(gfx, text);
                                break;
                            case SvgAnnotation svg:
                                DrawSvg(gfx, svg, fileCache);
                                break;
                        }
                    }
                }
            }

            outputDoc.Save(outputPath);
        }
        finally
        {
            outputDoc.Dispose();
            foreach (var doc in sourceDocCache.Values)
                doc.Dispose();
        }
    }

    private static void WithRotation(XGraphics gfx, Annotation ann, Action draw)
    {
        if (ann.Rotation != 0)
        {
            var state = gfx.Save();
            gfx.RotateAtTransform(ann.Rotation,
                new XPoint(ann.X + ann.WidthPt / 2.0, ann.Y + ann.HeightPt / 2.0));
            draw();
            gfx.Restore(state);
        }
        else draw();
    }

    private static void DrawText(XGraphics gfx, TextAnnotation text)
    {
        var font = new XFont(text.FontFamily, text.FontSize);
        var format = new XStringFormat
        {
            Alignment = XStringAlignment.Near,
            LineAlignment = XLineAlignment.Near
        };
        WithRotation(gfx, text, () =>
            gfx.DrawString(text.Text, font, XBrushes.Black, new XPoint(text.X, text.Y), format));
    }

    private static void DrawSvg(XGraphics gfx, SvgAnnotation svg, Dictionary<string, byte[]> fileCache)
    {
        if (svg.IsRaster)
        {
            DrawRasterImage(gfx, svg, fileCache);
            return;
        }

        double scale = svg.OriginalWidthPt > 0 ? svg.WidthPt / svg.OriginalWidthPt : svg.Scale;
        var pdfBytes = SvgRenderService.RenderToVectorPdf(svg.SvgFilePath, scale);
        if (pdfBytes.Length == 0) return;

        using var stream = new MemoryStream(pdfBytes);
        using var form = XPdfForm.FromStream(stream);
        WithRotation(gfx, svg, () =>
            gfx.DrawImage(form, svg.X, svg.Y, svg.WidthPt, svg.HeightPt));
    }

    private static void DrawRasterImage(XGraphics gfx, SvgAnnotation ann, Dictionary<string, byte[]> fileCache)
    {
        if (!fileCache.TryGetValue(ann.SvgFilePath, out var imageBytes))
        {
            imageBytes = File.ReadAllBytes(ann.SvgFilePath);
            fileCache[ann.SvgFilePath] = imageBytes;
        }
        if (imageBytes.Length == 0) return;

        using var stream = new MemoryStream(imageBytes);
        using var image = XImage.FromStream(stream);
        WithRotation(gfx, ann, () =>
            gfx.DrawImage(image, ann.X, ann.Y, ann.WidthPt, ann.HeightPt));
    }
}
