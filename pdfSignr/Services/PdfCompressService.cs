using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using PDFtoImage;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using pdfSignr.Models;

namespace pdfSignr.Services;

public enum CompressionPreset { Screen, Ebook, Print }

public record CompressResult(long OriginalSize, long CompressedSize, int PageCount, int ImagesResampled);

[SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
public static class PdfCompressService
{
    private static (int MaxDimension, int JpegQuality) GetPresetSettings(CompressionPreset preset) => preset switch
    {
        CompressionPreset.Screen => (1024, 50),
        CompressionPreset.Ebook => (1600, 65),
        CompressionPreset.Print => (2400, 80),
        _ => (1600, 65)
    };

    public static async Task<CompressResult> CompressAsync(
        IReadOnlyList<PageSource> pageSources,
        string outputPath,
        CompressionPreset preset,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var (maxDim, jpegQuality) = GetPresetSettings(preset);

        // Build the output PDF from page sources (same as save pipeline)
        // then walk its objects and resample embedded images
        var result = await Task.Run(() =>
        {
            // Import all pages into a new document
            var sourceDocCache = new Dictionary<int, PdfDocument>();
            var outputDoc = new PdfDocument();
            outputDoc.Options.CompressContentStreams = true;

            long originalSize = 0;
            var seenBytes = new HashSet<int>();
            foreach (var ps in pageSources)
            {
                var id = RuntimeHelpers.GetHashCode(ps.PdfBytes);
                if (seenBytes.Add(id))
                    originalSize += ps.PdfBytes.Length;
            }

            try
            {
                foreach (var source in pageSources)
                {
                    ct.ThrowIfCancellationRequested();
                    var id = RuntimeHelpers.GetHashCode(source.PdfBytes);
                    if (!sourceDocCache.TryGetValue(id, out var sourceDoc))
                    {
                        sourceDoc = PdfReader.Open(new MemoryStream(source.PdfBytes), PdfDocumentOpenMode.Import);
                        sourceDocCache[id] = sourceDoc;
                    }
                    outputDoc.AddPage(sourceDoc.Pages[source.SourcePageIndex]);
                }

                // Now walk all objects and resample images
                int imagesResampled = 0;
                var allObjects = outputDoc.Internals.GetAllObjects();
                int total = allObjects.Length;
                int processed = 0;

                foreach (var obj in allObjects)
                {
                    ct.ThrowIfCancellationRequested();
                    processed++;

                    if (obj is PdfDictionary dict &&
                        dict.Elements.GetName("/Subtype") == "/Image" &&
                        dict.Stream?.Value is { Length: > 0 } streamBytes)
                    {
                        var resampled = TryResampleImage(dict, streamBytes, maxDim, jpegQuality);
                        if (resampled) imagesResampled++;
                    }

                    if (processed % 20 == 0)
                        progress?.Report(processed * 100 / total);
                }

                progress?.Report(100);
                outputDoc.Save(outputPath);
                var compressedSize = new FileInfo(outputPath).Length;
                return new CompressResult(originalSize, compressedSize, pageSources.Count, imagesResampled);
            }
            finally
            {
                outputDoc.Dispose();
                foreach (var doc in sourceDocCache.Values)
                    doc.Dispose();
            }
        }, ct);

        return result;
    }

    private static bool TryResampleImage(PdfDictionary dict, byte[] streamBytes, int maxDim, int jpegQuality)
    {
        int width = dict.Elements.GetInteger("/Width");
        int height = dict.Elements.GetInteger("/Height");
        if (width <= 0 || height <= 0) return false;

        // Determine if the image is worth resampling:
        // - Skip tiny images (icons, logos) — not worth the quality loss
        // - Skip images that are already small enough
        if (width <= maxDim && height <= maxDim) return false;
        if (streamBytes.Length < 8192) return false; // skip very small streams

        try
        {
            // Try to decode the image stream
            byte[] imageBytes = GetDecodedImageBytes(dict, streamBytes, width, height);
            if (imageBytes.Length == 0) return false;

            // Decode into SKBitmap
            using var skBitmap = DecodeToSkBitmap(imageBytes, width, height, dict);
            if (skBitmap == null) return false;

            // Calculate new dimensions preserving aspect ratio
            double scale = Math.Min((double)maxDim / skBitmap.Width, (double)maxDim / skBitmap.Height);
            scale = Math.Min(scale, 1.0); // never upscale
            int newW = Math.Max(1, (int)(skBitmap.Width * scale));
            int newH = Math.Max(1, (int)(skBitmap.Height * scale));

            // Resize
            using var resized = skBitmap.Resize(new SKImageInfo(newW, newH), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            if (resized == null) return false;

            // Encode as JPEG
            using var jpegData = resized.Encode(SKEncodedImageFormat.Jpeg, jpegQuality);
            var jpegBytes = jpegData.ToArray();

            // Only replace if the new data is actually smaller
            if (jpegBytes.Length >= streamBytes.Length) return false;

            // Replace the stream and update metadata
            dict.Stream.Value = jpegBytes;
            dict.Elements.SetInteger("/Width", newW);
            dict.Elements.SetInteger("/Height", newH);
            dict.Elements.SetName("/Filter", "/DCTDecode");
            dict.Elements.SetInteger("/Length", jpegBytes.Length);
            dict.Elements.SetName("/ColorSpace", "/DeviceRGB");
            dict.Elements.SetInteger("/BitsPerComponent", 8);

            // Remove decode params that don't apply to JPEG
            dict.Elements.Remove("/DecodeParms");
            // Remove SMask/Mask if present (JPEG doesn't support transparency)
            // We keep them if they're separate objects — only remove inline references
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Image resample skipped: {ex.Message}");
            return false;
        }
    }

    private static byte[] GetDecodedImageBytes(PdfDictionary dict, byte[] streamBytes, int width, int height)
    {
        // Check if this is already a JPEG (DCTDecode) — we can decode directly with SkiaSharp
        string filter = dict.Elements.GetName("/Filter");
        if (filter is "/DCTDecode" or "/DCT")
            return streamBytes; // Already JPEG, SkiaSharp can decode directly

        // Try to get unfiltered bytes via PDFsharp
        if (dict.Stream != null)
        {
            try
            {
                if (dict.Stream.TryUncompress())
                    return dict.Stream.Value;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Image decode fallback: {ex.Message}"); }
        }

        return streamBytes;
    }

    private static SKBitmap? DecodeToSkBitmap(byte[] imageBytes, int width, int height, PdfDictionary dict)
    {
        string filter = dict.Elements.GetName("/Filter");

        // JPEG: decode directly
        if (filter is "/DCTDecode" or "/DCT")
            return SKBitmap.Decode(imageBytes);

        // Raw pixel data: reconstruct bitmap
        string colorSpace = dict.Elements.GetName("/ColorSpace");
        int bpc = dict.Elements.GetInteger("/BitsPerComponent");
        if (bpc <= 0) bpc = 8;

        if (colorSpace == "/DeviceRGB" && bpc == 8 && imageBytes.Length >= width * height * 3)
        {
            var bmp = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            var pixelData = new byte[width * height * 4];
            int srcIdx = 0;
            for (int i = 0; i < width * height; i++)
            {
                pixelData[i * 4 + 0] = imageBytes[srcIdx++]; // R
                pixelData[i * 4 + 1] = imageBytes[srcIdx++]; // G
                pixelData[i * 4 + 2] = imageBytes[srcIdx++]; // B
                pixelData[i * 4 + 3] = 255;                   // A
            }
            System.Runtime.InteropServices.Marshal.Copy(pixelData, 0, bmp.GetPixels(), pixelData.Length);
            return bmp;
        }

        if (colorSpace == "/DeviceGray" && bpc == 8 && imageBytes.Length >= width * height)
        {
            var bmp = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            var pixelData = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                byte g = imageBytes[i];
                pixelData[i * 4 + 0] = g;
                pixelData[i * 4 + 1] = g;
                pixelData[i * 4 + 2] = g;
                pixelData[i * 4 + 3] = 255;
            }
            System.Runtime.InteropServices.Marshal.Copy(pixelData, 0, bmp.GetPixels(), pixelData.Length);
            return bmp;
        }

        // Try generic SKBitmap decode as last resort (handles PNG, JPEG, etc.)
        return SKBitmap.Decode(imageBytes);
    }

    // ═══ Full-page rasterization (flatten for print) ═══

    private static (int Dpi, int JpegQuality) GetRasterSettings(CompressionPreset preset) => preset switch
    {
        CompressionPreset.Screen => (72, 50),
        CompressionPreset.Ebook => (150, 70),
        CompressionPreset.Print => (300, 90),
        _ => (150, 70)
    };

    public static async Task<CompressResult> RasterizeAsync(
        IReadOnlyList<PageSource> pageSources,
        string outputPath,
        CompressionPreset preset,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var (dpi, jpegQuality) = GetRasterSettings(preset);

        var seenBytes = new HashSet<int>();
        long originalSize = 0;
        foreach (var ps in pageSources)
        {
            var id = RuntimeHelpers.GetHashCode(ps.PdfBytes);
            if (seenBytes.Add(id))
                originalSize += ps.PdfBytes.Length;
        }

        var result = await Task.Run(() =>
        {
            var doc = new PdfDocument();
            doc.Options.CompressContentStreams = true;

            for (int i = 0; i < pageSources.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var source = pageSources[i];
                var (widthPt, heightPt) = PdfRenderService.GetPageSize(source.PdfBytes, source.SourcePageIndex);

                using var skBitmap = Conversion.ToImage(
                    source.PdfBytes, page: source.SourcePageIndex, options: new(Dpi: dpi));
                using var jpegData = skBitmap.Encode(SKEncodedImageFormat.Jpeg, jpegQuality);
                var jpegBytes = jpegData.ToArray();

                var page = doc.AddPage();
                page.Width = XUnitPt.FromPoint(widthPt);
                page.Height = XUnitPt.FromPoint(heightPt);

                using var imgStream = new MemoryStream(jpegBytes);
                var xImage = XImage.FromStream(imgStream);
                using var gfx = XGraphics.FromPdfPage(page);
                gfx.DrawImage(xImage, 0, 0, widthPt, heightPt);

                progress?.Report((i + 1) * 100 / pageSources.Count);
            }

            doc.Save(outputPath);
            doc.Dispose();

            var compressedSize = new FileInfo(outputPath).Length;
            return new CompressResult(originalSize, compressedSize, pageSources.Count, 0);
        }, ct);

        return result;
    }
}
