using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using PDFtoImage;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using pdfSignr.Models;

namespace pdfSignr.Services;

/// <summary>Image resampling quality preset for PDF compression.</summary>
public enum CompressionPreset { Screen, Ebook, Print }

/// <summary>Result of a PDF compression or rasterization operation.</summary>
public record CompressResult(long OriginalSize, long CompressedSize, int PageCount, int ImagesResampled);

[SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
/// <summary>Compresses PDF files by resampling embedded images or rasterizing pages.</summary>
public class PdfCompressService : IPdfCompressService
{
    private readonly IPdfRenderService _renderService;
    private readonly ISettingsService _settings;
    private readonly ILogger<PdfCompressService> _logger;

    public PdfCompressService(IPdfRenderService renderService, ISettingsService settings, ILogger<PdfCompressService> logger)
    {
        _renderService = renderService;
        _settings = settings;
        _logger = logger;
    }

    private record PresetConfig(int ResampleMaxDim, int ResampleQuality, int RasterDpi, int RasterQuality);

    private PresetConfig GetPreset(CompressionPreset preset)
    {
        var dpi = _settings.Current.CompressDpi;
        return preset switch
        {
            CompressionPreset.Screen => new(dpi.ScreenMaxDim, 50, 72, 50),
            CompressionPreset.Ebook  => new(dpi.EbookMaxDim, 65, 150, 70),
            CompressionPreset.Print  => new(dpi.PrintMaxDim, 80, 300, 90),
            _                        => new(dpi.EbookMaxDim, 65, 150, 70),
        };
    }

    private static long ComputeOriginalSize(IReadOnlyList<(PageSource Source, int RotationDegrees)> pageSources)
    {
        long total = 0;
        var seen = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        foreach (var (ps, _) in pageSources)
            if (seen.Add(ps.PdfBytes))
                total += ps.PdfBytes.Length;
        return total;
    }

    /// <summary>Compresses a PDF by resampling embedded images that exceed the preset dimensions.</summary>
    public async Task<CompressResult> CompressAsync(
        IReadOnlyList<(PageSource Source, int RotationDegrees)> pageSources,
        string outputPath,
        CompressionPreset preset,
        string? outputPassword = null,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var config = GetPreset(preset);
        int maxDim = config.ResampleMaxDim, jpegQuality = config.ResampleQuality;
        long originalSize = ComputeOriginalSize(pageSources);

        var result = await Task.Run(() =>
        {
            var sourceDocCache = new Dictionary<byte[], PdfDocument>(ReferenceEqualityComparer.Instance);
            var outputDoc = new PdfDocument();
            outputDoc.Options.CompressContentStreams = true;

            try
            {
                foreach (var (source, rotDeg) in pageSources)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!sourceDocCache.TryGetValue(source.PdfBytes, out var sourceDoc))
                    {
                        sourceDoc = string.IsNullOrEmpty(source.Password)
                            ? PdfReader.Open(new MemoryStream(source.PdfBytes), PdfDocumentOpenMode.Import)
                            : PdfReader.Open(new MemoryStream(source.PdfBytes), source.Password, PdfDocumentOpenMode.Import);
                        sourceDocCache[source.PdfBytes] = sourceDoc;
                    }
                    var page = outputDoc.AddPage(sourceDoc.Pages[source.SourcePageIndex]);
                    if (rotDeg != 0)
                        page.Rotate = (page.Rotate + rotDeg) % 360;
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

                if (!string.IsNullOrEmpty(outputPassword))
                {
                    outputDoc.SecuritySettings.UserPassword = outputPassword;
                    outputDoc.SecuritySettings.OwnerPassword = outputPassword;
                }

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

        // Try to get unfiltered bytes via PDFsharp — work on a copy to avoid mutating the original
        if (dict.Stream != null)
        {
            try
            {
                var copy = (byte[])streamBytes.Clone();
                var original = dict.Stream.Value;
                dict.Stream.Value = copy;
                if (dict.Stream.TryUncompress())
                {
                    var uncompressed = dict.Stream.Value;
                    dict.Stream.Value = original; // restore original stream
                    return uncompressed;
                }
                dict.Stream.Value = original; // restore original stream
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Image decode fallback: {ex.Message}");
            }
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
            try
            {
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
            catch { bmp.Dispose(); return null; }
        }

        if (colorSpace == "/DeviceGray" && bpc == 8 && imageBytes.Length >= width * height)
        {
            var bmp = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            try
            {
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
            catch { bmp.Dispose(); return null; }
        }

        // Try generic SKBitmap decode as last resort (handles PNG, JPEG, etc.)
        return SKBitmap.Decode(imageBytes);
    }

    /// <summary>Rasterizes each PDF page to a JPEG image at the preset DPI, producing a flattened PDF.</summary>
    public async Task<CompressResult> RasterizeAsync(
        IReadOnlyList<(PageSource Source, int RotationDegrees)> pageSources,
        string outputPath,
        CompressionPreset preset,
        string? outputPassword = null,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var config = GetPreset(preset);
        int dpi = config.RasterDpi, jpegQuality = config.RasterQuality;
        long originalSize = ComputeOriginalSize(pageSources);

        var result = await Task.Run(() =>
        {
            using var doc = new PdfDocument();
            doc.Options.CompressContentStreams = true;

            for (int i = 0; i < pageSources.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var (source, rotDeg) = pageSources[i];
                var (widthPt, heightPt) = _renderService.GetPageSize(source.PdfBytes, source.SourcePageIndex, source.Password);

                // Apply rotation to dimensions and rendering
                var rotation = PdfConstants.ToPdfRotation(rotDeg);
                double effectiveW = rotDeg is 90 or 270 ? heightPt : widthPt;
                double effectiveH = rotDeg is 90 or 270 ? widthPt : heightPt;

                using var skBitmap = Conversion.ToImage(
                    source.PdfBytes, page: source.SourcePageIndex, password: source.Password, options: new(Dpi: dpi, Rotation: rotation));
                using var jpegData = skBitmap.Encode(SKEncodedImageFormat.Jpeg, jpegQuality);
                var jpegBytes = jpegData.ToArray();

                var page = doc.AddPage();
                page.Width = XUnitPt.FromPoint(effectiveW);
                page.Height = XUnitPt.FromPoint(effectiveH);

                using var imgStream = new MemoryStream(jpegBytes);
                using var xImage = XImage.FromStream(imgStream);
                using var gfx = XGraphics.FromPdfPage(page);
                gfx.DrawImage(xImage, 0, 0, effectiveW, effectiveH);

                progress?.Report((i + 1) * 100 / pageSources.Count);
            }

            if (!string.IsNullOrEmpty(outputPassword))
            {
                doc.SecuritySettings.UserPassword = outputPassword;
                doc.SecuritySettings.OwnerPassword = outputPassword;
            }

            doc.Save(outputPath);

            var compressedSize = new FileInfo(outputPath).Length;
            return new CompressResult(originalSize, compressedSize, pageSources.Count, 0);
        }, ct);

        return result;
    }
}
