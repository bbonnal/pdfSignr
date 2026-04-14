using System.Runtime.CompilerServices;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using pdfSignr.Models;

namespace pdfSignr.Services;

public static class PdfSaveService
{
    public static void Save(
        string outputPath,
        IEnumerable<(PageSource Source, IEnumerable<Annotation> Annotations)> pages)
    {
        var sourceDocCache = new Dictionary<byte[], PdfDocument>(ReferenceEqualityComparer.Instance);
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
                                DrawSvg(gfx, svg);
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

    /// <summary>Equality comparer that uses reference identity for byte arrays.</summary>
    private sealed class ReferenceEqualityComparer : IEqualityComparer<byte[]>
    {
        public static readonly ReferenceEqualityComparer Instance = new();
        public bool Equals(byte[]? x, byte[]? y) => ReferenceEquals(x, y);
        public int GetHashCode(byte[] obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private static void DrawText(XGraphics gfx, TextAnnotation text)
    {
        var font = new XFont(text.FontFamily, text.FontSize);
        var format = new XStringFormat
        {
            Alignment = XStringAlignment.Near,
            LineAlignment = XLineAlignment.Near
        };

        if (text.Rotation != 0)
        {
            double cx = text.X + text.WidthPt / 2.0;
            double cy = text.Y + text.HeightPt / 2.0;
            var state = gfx.Save();
            gfx.RotateAtTransform(text.Rotation, new XPoint(cx, cy));
            gfx.DrawString(text.Text, font, XBrushes.Black, new XPoint(text.X, text.Y), format);
            gfx.Restore(state);
        }
        else
        {
            gfx.DrawString(text.Text, font, XBrushes.Black, new XPoint(text.X, text.Y), format);
        }
    }

    private static void DrawSvg(XGraphics gfx, SvgAnnotation svg)
    {
        // Re-derive scale from current dimensions
        double scale = svg.OriginalWidthPt > 0 ? svg.WidthPt / svg.OriginalWidthPt : svg.Scale;
        var pngBytes = SvgRenderService.RenderToPng(svg.SvgFilePath, scale, 300);
        if (pngBytes.Length == 0) return;

        using var stream = new MemoryStream(pngBytes);
        using var image = XImage.FromStream(stream);

        if (svg.Rotation != 0)
        {
            double cx = svg.X + svg.WidthPt / 2.0;
            double cy = svg.Y + svg.HeightPt / 2.0;
            var state = gfx.Save();
            gfx.RotateAtTransform(svg.Rotation, new XPoint(cx, cy));
            gfx.DrawImage(image, svg.X, svg.Y, svg.WidthPt, svg.HeightPt);
            gfx.Restore(state);
        }
        else
        {
            gfx.DrawImage(image, svg.X, svg.Y, svg.WidthPt, svg.HeightPt);
        }
    }
}
