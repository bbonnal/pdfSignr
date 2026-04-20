using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using pdfSignr.Models;
using pdfSignr.Services;
using Xunit;

namespace pdfSignr.Tests;

public class PdfCompressServiceTests : IDisposable
{
    private readonly string _dir;

    public PdfCompressServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pdfSignr-compress-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }

    private static byte[] MakeBlankPdf(int pageCount, double widthPt = 612, double heightPt = 792)
    {
        using var doc = new PdfDocument();
        for (int i = 0; i < pageCount; i++)
        {
            var page = doc.AddPage();
            page.Width = PdfSharp.Drawing.XUnitPt.FromPoint(widthPt);
            page.Height = PdfSharp.Drawing.XUnitPt.FromPoint(heightPt);
        }
        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    private static PdfCompressService NewService()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "pdfSignr-compress-settings-" + Guid.NewGuid().ToString("N") + ".json");
        var settings = new SettingsService(settingsPath, NullLogger<SettingsService>.Instance);
        return new PdfCompressService(new FakeRenderService(), settings, NullLogger<PdfCompressService>.Instance);
    }

    [Fact]
    public async Task CompressAsync_NoImages_ProducesValidPdf()
    {
        var bytes = MakeBlankPdf(2);
        var outPath = Path.Combine(_dir, "compressed.pdf");
        var pages = new List<(PageSource, int)>
        {
            (new PageSource(bytes, 0), 0),
            (new PageSource(bytes, 1), 0),
        };

        var result = await NewService().CompressAsync(pages, outPath, CompressionPreset.Ebook);

        Assert.Equal(2, result.PageCount);
        Assert.Equal(0, result.ImagesResampled);
        Assert.True(File.Exists(outPath));
        using var read = PdfReader.Open(outPath, PdfDocumentOpenMode.Import);
        Assert.Equal(2, read.PageCount);
    }

    [Fact]
    public async Task RasterizeAsync_ProducesSinglePagePerInputPage()
    {
        var bytes = MakeBlankPdf(3);
        var outPath = Path.Combine(_dir, "rasterized.pdf");
        var pages = Enumerable.Range(0, 3)
            .Select(i => (new PageSource(bytes, i), 0))
            .ToList();

        var result = await NewService().RasterizeAsync(pages, outPath, CompressionPreset.Screen);

        Assert.Equal(3, result.PageCount);
        using var read = PdfReader.Open(outPath, PdfDocumentOpenMode.Import);
        Assert.Equal(3, read.PageCount);
    }

    [Fact]
    public async Task RasterizeAsync_WithRotation_SwapsPageOrientation()
    {
        var bytes = MakeBlankPdf(1, widthPt: 400, heightPt: 800);
        var outPath = Path.Combine(_dir, "rotated.pdf");
        var pages = new List<(PageSource, int)> { (new PageSource(bytes, 0), 90) };

        await NewService().RasterizeAsync(pages, outPath, CompressionPreset.Screen);

        using var read = PdfReader.Open(outPath, PdfDocumentOpenMode.Import);
        var page = read.Pages[0];
        Assert.True(page.Width.Point > page.Height.Point,
            $"Expected rotated page to be landscape; got {page.Width.Point}x{page.Height.Point}");
    }

    private sealed class FakeRenderService : IPdfRenderService
    {
        public int GetPageCount(byte[] pdfBytes, string? password = null)
        {
            using var ms = new MemoryStream(pdfBytes);
            using var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
            return doc.PageCount;
        }
        public (double WidthPt, double HeightPt) GetPageSize(byte[] pdfBytes, int pageIndex, string? password = null)
        {
            using var ms = new MemoryStream(pdfBytes);
            using var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
            var p = doc.Pages[pageIndex];
            return (p.Width.Point, p.Height.Point);
        }
        public IReadOnlyList<(double WidthPt, double HeightPt)> GetAllPageSizes(byte[] pdfBytes, string? password = null)
        {
            using var ms = new MemoryStream(pdfBytes);
            using var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
            return Enumerable.Range(0, doc.PageCount)
                .Select(i => (doc.Pages[i].Width.Point, doc.Pages[i].Height.Point))
                .ToList();
        }
        public Bitmap RenderPage(byte[] pdfBytes, int pageIndex, int dpi, int rotationDegrees = 0, string? password = null) =>
            throw new NotSupportedException();
    }
}
