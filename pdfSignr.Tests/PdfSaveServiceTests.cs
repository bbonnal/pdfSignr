using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using pdfSignr.Models;
using pdfSignr.Services;
using Xunit;

namespace pdfSignr.Tests;

public class PdfSaveServiceTests : IDisposable
{
    private readonly string _dir;

    public PdfSaveServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pdfSignr-save-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }

    private static byte[] MakeBlankPdf(int pageCount)
    {
        using var doc = new PdfDocument();
        for (int i = 0; i < pageCount; i++) doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    private static PdfSaveService NewService() =>
        new(new FakeSvgRenderService(), NullLogger<PdfSaveService>.Instance);

    private static IEnumerable<(PageSource, int, IEnumerable<Annotation>)>
        ToPages(byte[] pdfBytes, int count, int rotation = 0) =>
        Enumerable.Range(0, count).Select(i =>
            ((PageSource, int, IEnumerable<Annotation>))
            (new PageSource(pdfBytes, i), rotation, Array.Empty<Annotation>()));

    [Fact]
    public async Task SaveAsync_NoAnnotations_RoundTripsPageCount()
    {
        var bytes = MakeBlankPdf(3);
        var outPath = Path.Combine(_dir, "out.pdf");

        var result = await NewService().SaveAsync(outPath, ToPages(bytes, 3));

        Assert.Equal(3, result.PagesWritten);
        Assert.True(File.Exists(outPath));
        using var read = PdfReader.Open(outPath, PdfDocumentOpenMode.Import);
        Assert.Equal(3, read.PageCount);
    }

    [Fact]
    public async Task SaveAsync_Cancelled_DeletesPartialOutput()
    {
        var bytes = MakeBlankPdf(5);
        var outPath = Path.Combine(_dir, "cancelled.pdf");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewService().SaveAsync(outPath, ToPages(bytes, 5), ct: cts.Token));

        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public async Task SaveAsync_WithOutputPassword_ProducesEncryptedPdf()
    {
        var bytes = MakeBlankPdf(1);
        var outPath = Path.Combine(_dir, "encrypted.pdf");

        await NewService().SaveAsync(outPath, ToPages(bytes, 1), outputPassword: "secret");

        Assert.Throws<PdfReaderException>(() =>
            PdfReader.Open(outPath, PdfDocumentOpenMode.Import));
        using var opened = PdfReader.Open(outPath, "secret", PdfDocumentOpenMode.Import);
        Assert.Equal(1, opened.PageCount);
    }

    private sealed class FakeSvgRenderService : ISvgRenderService
    {
        public (double WidthPt, double HeightPt) GetSvgSize(string svgPath) => (0, 0);
        public (double WidthPt, double HeightPt) GetImageSize(string path) => (0, 0);
        public Bitmap RenderForDisplay(string svgPath, double scale, int renderDpi) =>
            throw new NotSupportedException();
        public (double WidthPt, double HeightPt, Bitmap Bitmap) GetSizeAndRenderForDisplay(
            string svgPath, double scale, int renderDpi) => throw new NotSupportedException();
        public Bitmap ResampleForDisplay(string path, double widthPt, double heightPt, int dpi) =>
            throw new NotSupportedException();
        public byte[] RenderToVectorPdf(string svgPath, double scale) => Array.Empty<byte>();
        public Bitmap RenderForAnnotation(SvgAnnotation annotation, int dpi) =>
            throw new NotSupportedException();
    }

}
