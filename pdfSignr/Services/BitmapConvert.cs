using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace pdfSignr.Services;

/// <summary>
/// Converts SkiaSharp bitmaps to Avalonia bitmaps via direct pixel copy,
/// avoiding the expensive PNG encode/decode round-trip.
/// </summary>
internal static class BitmapConvert
{
    public static unsafe WriteableBitmap ToAvaloniaBitmap(SKBitmap skBitmap)
    {
        // Ensure source is in Bgra8888/Premul to match Avalonia's native format
        SKBitmap source = skBitmap;
        bool needsDispose = false;
        if (skBitmap.ColorType != SKColorType.Bgra8888 || skBitmap.AlphaType != SKAlphaType.Premul)
        {
            source = new SKBitmap(skBitmap.Width, skBitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(source);
            canvas.DrawBitmap(skBitmap, 0, 0);
            needsDispose = true;
        }

        try
        {
            var wb = new WriteableBitmap(
                new PixelSize(source.Width, source.Height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            using (var fb = wb.Lock())
            {
                var srcPtr = (byte*)source.GetPixels().ToPointer();
                var dstPtr = (byte*)fb.Address.ToPointer();
                int srcStride = source.RowBytes;
                int dstStride = fb.RowBytes;

                if (srcStride == dstStride)
                {
                    Buffer.MemoryCopy(srcPtr, dstPtr, srcStride * source.Height, srcStride * source.Height);
                }
                else
                {
                    int copyWidth = Math.Min(srcStride, dstStride);
                    for (int y = 0; y < source.Height; y++)
                    {
                        Buffer.MemoryCopy(
                            srcPtr + y * srcStride,
                            dstPtr + y * dstStride,
                            copyWidth, copyWidth);
                    }
                }
            }

            return wb;
        }
        finally
        {
            if (needsDispose) source.Dispose();
        }
    }
}
