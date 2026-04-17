using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using pdfSignr.Models;

namespace pdfSignr.Services;

/// <summary>Saves annotated PDF pages to disk using PDFsharp, embedding text and image annotations.</summary>
public class PdfSaveService : IPdfSaveService
{
    /// <summary>Saves multiple pages with annotations to a new PDF file.</summary>
    public void Save(
        string outputPath,
        IEnumerable<(PageSource Source, int RotationDegrees, double OriginalWidthPt, double OriginalHeightPt,
            IEnumerable<Annotation> Annotations)> pages,
        string? outputPassword = null)
    {
        var sourceDocCache = new Dictionary<byte[], PdfDocument>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        var fileCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var outputDoc = new PdfDocument();

        try
        {
            foreach (var (source, rotationDegrees, origW, origH, annotations) in pages)
            {
                if (!sourceDocCache.TryGetValue(source.PdfBytes, out var sourceDoc))
                {
                    // PdfReader reads the stream eagerly in Import mode; stream lifetime is managed by PdfDocument.Dispose()
                    sourceDoc = string.IsNullOrEmpty(source.Password)
                        ? PdfReader.Open(new MemoryStream(source.PdfBytes), PdfDocumentOpenMode.Import)
                        : PdfReader.Open(new MemoryStream(source.PdfBytes), source.Password, PdfDocumentOpenMode.Import);
                    sourceDocCache[source.PdfBytes] = sourceDoc;
                }

                var importedPage = outputDoc.AddPage(sourceDoc.Pages[source.SourcePageIndex]);

                if (rotationDegrees != 0)
                    importedPage.Rotate = (importedPage.Rotate + rotationDegrees) % 360;

                var annList = annotations.ToList();
                if (annList.Count > 0)
                {
                    using var gfx = XGraphics.FromPdfPage(importedPage, XGraphicsPdfPageOptions.Append);
                    foreach (var annotation in annList)
                    {
                        // Inverse-transform annotation coordinates from rotated view back to original page space
                        var (drawX, drawY) = InverseTransformAnnotation(
                            annotation.X, annotation.Y, annotation.WidthPt, annotation.HeightPt,
                            rotationDegrees, origW, origH);

                        switch (annotation)
                        {
                            case TextAnnotation text:
                                DrawText(gfx, text, drawX, drawY);
                                break;
                            case SvgAnnotation svg:
                                DrawSvg(gfx, svg, drawX, drawY, fileCache);
                                break;
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(outputPassword))
            {
                outputDoc.SecuritySettings.UserPassword = outputPassword;
                outputDoc.SecuritySettings.OwnerPassword = outputPassword;
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

    private static (double x, double y) InverseTransformAnnotation(
        double annX, double annY, double annW, double annH,
        int rotationDegrees, double origW, double origH)
    {
        return rotationDegrees switch
        {
            90  => (annY, origH - annX - annH),
            180 => (origW - annX - annW, origH - annY - annH),
            270 => (origW - annY - annW, annX),
            _   => (annX, annY)
        };
    }

    private static void WithRotation(XGraphics gfx, Annotation ann, double drawX, double drawY, Action draw)
    {
        if (ann.Rotation != 0)
        {
            var state = gfx.Save();
            gfx.RotateAtTransform(ann.Rotation,
                new XPoint(drawX + ann.WidthPt / 2.0, drawY + ann.HeightPt / 2.0));
            draw();
            gfx.Restore(state);
        }
        else draw();
    }

    private static void DrawText(XGraphics gfx, TextAnnotation text, double drawX, double drawY)
    {
        var font = new XFont(text.FontFamily, text.FontSize);
        var format = new XStringFormat
        {
            Alignment = XStringAlignment.Near,
            LineAlignment = XLineAlignment.Near
        };
        WithRotation(gfx, text, drawX, drawY, () =>
            gfx.DrawString(text.Text, font, XBrushes.Black, new XPoint(drawX, drawY), format));
    }

    private static void DrawSvg(XGraphics gfx, SvgAnnotation svg, double drawX, double drawY,
        Dictionary<string, byte[]> fileCache)
    {
        if (svg.IsRaster)
        {
            DrawRasterImage(gfx, svg, drawX, drawY, fileCache);
            return;
        }

        double scale = svg.OriginalWidthPt > 0 ? svg.WidthPt / svg.OriginalWidthPt : svg.Scale;
        var pdfBytes = SvgRenderService.RenderToVectorPdf(svg.SvgFilePath, scale);
        if (pdfBytes.Length == 0) return;

        using var stream = new MemoryStream(pdfBytes);
        using var form = XPdfForm.FromStream(stream);
        WithRotation(gfx, svg, drawX, drawY, () =>
            gfx.DrawImage(form, drawX, drawY, svg.WidthPt, svg.HeightPt));
    }

    private static void DrawRasterImage(XGraphics gfx, SvgAnnotation ann, double drawX, double drawY,
        Dictionary<string, byte[]> fileCache)
    {
        if (!fileCache.TryGetValue(ann.SvgFilePath, out var imageBytes))
        {
            imageBytes = File.ReadAllBytes(ann.SvgFilePath);
            fileCache[ann.SvgFilePath] = imageBytes;
        }
        if (imageBytes.Length == 0) return;

        using var stream = new MemoryStream(imageBytes);
        using var image = XImage.FromStream(stream);
        WithRotation(gfx, ann, drawX, drawY, () =>
            gfx.DrawImage(image, drawX, drawY, ann.WidthPt, ann.HeightPt));
    }
}
