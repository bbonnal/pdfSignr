using Microsoft.Extensions.Logging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using pdfSignr.Models;

namespace pdfSignr.Services;

/// <summary>Saves annotated PDF pages to disk using PDFsharp, embedding text and image annotations.</summary>
public class PdfSaveService : IPdfSaveService
{
    private readonly ISvgRenderService _svgRenderer;
    private readonly ILogger<PdfSaveService> _logger;

    public PdfSaveService(ISvgRenderService svgRenderer, ILogger<PdfSaveService> logger)
    {
        _svgRenderer = svgRenderer;
        _logger = logger;
    }

    public async Task<SaveResult> SaveAsync(
        string outputPath,
        IEnumerable<(PageSource Source, int RotationDegrees, double OriginalWidthPt, double OriginalHeightPt,
            IEnumerable<Annotation> Annotations)> pages,
        string? outputPassword = null,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var pageList = pages.ToList();
        int totalPages = pageList.Count;

        return await Task.Run(() =>
        {
            var sourceDocCache = new Dictionary<byte[], PdfDocument>(ReferenceEqualityComparer.Instance);
            var sourceStreams = new List<MemoryStream>();
            var fileCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var outputDoc = new PdfDocument();

            try
            {
                for (int pageIdx = 0; pageIdx < totalPages; pageIdx++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (source, rotationDegrees, origW, origH, annotations) = pageList[pageIdx];

                    if (!sourceDocCache.TryGetValue(source.PdfBytes, out var sourceDoc))
                    {
                        var ms = new MemoryStream(source.PdfBytes);
                        sourceStreams.Add(ms);
                        sourceDoc = string.IsNullOrEmpty(source.Password)
                            ? PdfReader.Open(ms, PdfDocumentOpenMode.Import)
                            : PdfReader.Open(ms, source.Password, PdfDocumentOpenMode.Import);
                        sourceDocCache[source.PdfBytes] = sourceDoc;
                    }

                    var sourcePage = sourceDoc.Pages[source.SourcePageIndex];
                    var importedPage = outputDoc.AddPage(sourcePage);

                    // Total rotation the saved page will be viewed at = source's intrinsic /Rotate
                    // plus the user's in-app rotation.
                    int sourceRotation = ((sourcePage.Rotate % 360) + 360) % 360;
                    int totalRotation = ((sourceRotation + rotationDegrees) % 360 + 360) % 360;
                    if (rotationDegrees != 0)
                        importedPage.Rotate = totalRotation;

                    var annList = annotations.ToList();

                    _logger.LogInformation(
                        "Save page {PageIdx}: sourceRotate={SrcRot}, userRotate={UserRot}, totalRotation={Total}, " +
                        "underlying size={UW}x{UH}pt (from sourcePage), origW/H (from pdfium)={OrigW}x{OrigH}, annCount={N}",
                        pageIdx, sourceRotation, rotationDegrees, totalRotation,
                        sourcePage.Width.Point, sourcePage.Height.Point, origW, origH, annList.Count);
                    if (annList.Count > 0)
                    {
                        using var gfx = XGraphics.FromPdfPage(importedPage, XGraphicsPdfPageOptions.Append);

                        // Annotations are stored in the user's visual (post-total-rotation) coord
                        // system. XGraphics.FromPdfPage draws in the underlying (pre-rotation) page
                        // space, so we apply a transform that makes drawing at visual (X, Y) land
                        // correctly after the viewer applies /Rotate.
                        double underlyingW = sourcePage.Width.Point;
                        double underlyingH = sourcePage.Height.Point;
                        ApplyVisualTransform(gfx, totalRotation, underlyingW, underlyingH);

                        foreach (var annotation in annList)
                        {
                            switch (annotation)
                            {
                                case TextAnnotation text:
                                    DrawText(gfx, text, annotation.X, annotation.Y);
                                    break;
                                case SvgAnnotation svg:
                                    DrawSvg(gfx, svg, annotation.X, annotation.Y, fileCache, _svgRenderer);
                                    break;
                            }
                        }
                    }

                    progress?.Report((pageIdx + 1) * 100 / totalPages);
                }

                if (!string.IsNullOrEmpty(outputPassword))
                {
                    outputDoc.SecuritySettings.UserPassword = outputPassword;
                    outputDoc.SecuritySettings.OwnerPassword = outputPassword;
                }

                outputDoc.Save(outputPath);
                long outputSize = new FileInfo(outputPath).Length;
                return new SaveResult(totalPages, outputSize);
            }
            catch (OperationCanceledException)
            {
                TryDeletePartial(outputPath);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PDF save failed for {Path}", outputPath);
                TryDeletePartial(outputPath);
                throw;
            }
            finally
            {
                outputDoc.Dispose();
                foreach (var doc in sourceDocCache.Values)
                    doc.Dispose();
                foreach (var s in sourceStreams)
                    s.Dispose();
            }
        }, ct);
    }

    private void TryDeletePartial(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete partial output file {Path}", path);
        }
    }

    // Maps the XGraphics coord system from the underlying page space to the visual
    // (post-/Rotate) space. After this, drawing at (vx, vy) in visual coords appears at
    // (vx, vy) visually once the viewer applies the page's /Rotate.
    private static void ApplyVisualTransform(XGraphics gfx, int totalRotation, double underlyingW, double underlyingH)
    {
        switch (totalRotation)
        {
            case 90:
                gfx.TranslateTransform(0, underlyingH);
                gfx.RotateTransform(-90);
                break;
            case 180:
                gfx.TranslateTransform(underlyingW, underlyingH);
                gfx.RotateTransform(180);
                break;
            case 270:
                gfx.TranslateTransform(underlyingW, 0);
                gfx.RotateTransform(90);
                break;
        }
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
        Dictionary<string, byte[]> fileCache, ISvgRenderService svgRenderer)
    {
        if (svg.IsRaster)
        {
            DrawRasterImage(gfx, svg, drawX, drawY, fileCache);
            return;
        }

        double scale = svg.OriginalWidthPt > 0 ? svg.WidthPt / svg.OriginalWidthPt : svg.Scale;
        var pdfBytes = svgRenderer.RenderToVectorPdf(svg.SvgFilePath, scale);
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
