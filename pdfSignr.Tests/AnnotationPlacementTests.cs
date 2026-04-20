using System.Text;
using Avalonia;
using Avalonia.Headless;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using pdfSignr.Models;
using pdfSignr.Services;
using Xunit;
using Xunit.Abstractions;

namespace pdfSignr.Tests;

// Per-assembly singleton Avalonia headless app — needed so TextAnnotation.ComputeFontSize()
// can call Avalonia's FormattedText inside tests.
public sealed class AvaloniaAppFixture : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _uiThread;

    public AvaloniaAppFixture()
    {
        var started = new ManualResetEventSlim();
        _uiThread = new Thread(() =>
        {
            AppBuilder.Configure<HeadlessApp>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .SetupWithoutStarting();
            started.Set();
            // Keep the message loop alive for the duration of the test assembly.
            Avalonia.Threading.Dispatcher.UIThread.MainLoop(_cts.Token);
        }) { IsBackground = true };
        _uiThread.Start();
        started.Wait();
    }

    public void Dispose() => _cts.Cancel();

    private sealed class HeadlessApp : Application
    {
        public override void Initialize() { }
    }
}

[CollectionDefinition("Avalonia")]
public sealed class AvaloniaCollection : ICollectionFixture<AvaloniaAppFixture> { }

[Collection("Avalonia")]
public class AnnotationPlacementTests : IDisposable
{
    private readonly string _dir;
    private readonly ITestOutputHelper _output;

    public AnnotationPlacementTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "pdfSignr-place-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var catalog = new FontCatalog();
        if (GlobalFontSettings.FontResolver == null)
            GlobalFontSettings.FontResolver = new FontResolver(catalog);
        TextAnnotation.Catalog = catalog;
    }

    public void Dispose()
    {
        // Preserve output files for manual inspection. Path is logged.
        _output.WriteLine($"Test artifacts: {_dir}");
    }

    private static byte[] MakeBlankPdf()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = PdfSharp.Drawing.XUnit.FromPoint(612);
        page.Height = PdfSharp.Drawing.XUnit.FromPoint(792);
        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    private static void RenderAndSavePng(string pdfPath, string pngPath)
    {
        using var ms = new MemoryStream(File.ReadAllBytes(pdfPath));
        using var skBitmap = PDFtoImage.Conversion.ToImage(
            File.ReadAllBytes(pdfPath), 0, null,
            new PDFtoImage.RenderOptions(Dpi: 96));
        using var img = SkiaSharp.SKImage.FromBitmap(skBitmap);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        using var fs = File.Create(pngPath);
        data.SaveTo(fs);
    }

    [Fact]
    public async Task TextAnnotation_PdfContent_ShowsCoordinates()
    {
        var bytes = MakeBlankPdf();
        var outPath = Path.Combine(_dir, "text.pdf");

        var text = new TextAnnotation
        {
            X = 300, Y = 400,
            WidthPt = 50, HeightPt = 18,
            Text = "HELLO",
            FontFamily = "Helvetica",
            PageIndex = 0
        };

        var pages = new[]
        {
            ((PageSource, int, double, double, IEnumerable<Annotation>))
            (new PageSource(bytes, 0), 0, 612.0, 792.0, new Annotation[] { text })
        };

        var svc = new PdfSaveService(new NullSvgRenderService(), NullLogger<PdfSaveService>.Instance);
        await svc.SaveAsync(outPath, pages);

        Assert.True(File.Exists(outPath));

        using var read = PdfReader.Open(outPath, PdfDocumentOpenMode.Modify);
        var outPage = read.Pages[0];
        var content = ExtractContentStreams(outPage);
        _output.WriteLine("Input: X=300 Y=400 (GDI top-left origin), Page 612x792");
        _output.WriteLine($"Output file: {new FileInfo(outPath).Length} bytes");
        _output.WriteLine("--- content streams ---");
        _output.WriteLine(content);

        var pngPath = Path.Combine(_dir, "text.png");
        RenderAndSavePng(outPath, pngPath);
        _output.WriteLine($"Rendered PNG: {pngPath}");
    }

    [Fact]
    public async Task Diagnostic_AnnotationsAtKnownCorners()
    {
        // Place marker annotations at known Y positions on an UNROTATED source page.
        // We then render to PNG and inspect visually. Helps isolate whether the bug
        // is in the unrotated path.
        var bytes = MakeBlankPdf();
        var outPath = Path.Combine(_dir, "corners.pdf");

        var anns = new List<Annotation>();
        foreach (var y in new[] { 50.0, 200.0, 400.0, 600.0, 750.0 })
        {
            anns.Add(new TextAnnotation
            {
                X = 100, Y = y,
                WidthPt = 200, HeightPt = 18,
                Text = $"Y={y:F0}",
                FontFamily = "Helvetica",
                PageIndex = 0
            });
        }

        var pages = new[]
        {
            ((PageSource, int, double, double, IEnumerable<Annotation>))
            (new PageSource(bytes, 0), 0, 612.0, 792.0, (IEnumerable<Annotation>)anns)
        };

        var svc = new PdfSaveService(new NullSvgRenderService(), NullLogger<PdfSaveService>.Instance);
        await svc.SaveAsync(outPath, pages);

        var pngPath = Path.Combine(_dir, "corners.png");
        RenderAndSavePng(outPath, pngPath);
        _output.WriteLine($"Rendered: {pngPath}");
        _output.WriteLine("Unrotated page 612×792. Expected text at X=100 and each of Y=50, 200, 400, 600, 750.");
    }

    [Fact]
    public async Task Diagnostic_AnnotationsAtKnownCorners_SourceRotate90()
    {
        // Same diagnostic but source page has /Rotate 90.
        byte[] bytes;
        using (var doc = new PdfDocument())
        {
            var p = doc.AddPage();
            p.Width = PdfSharp.Drawing.XUnit.FromPoint(612);
            p.Height = PdfSharp.Drawing.XUnit.FromPoint(792);
            p.Rotate = 90;
            using var ms = new MemoryStream();
            doc.Save(ms, closeStream: false);
            bytes = ms.ToArray();
        }

        var svc2 = new PdfRenderService();
        var (pdfW, pdfH) = svc2.GetPageSize(bytes, 0);
        _output.WriteLine($"pdfium dims: {pdfW} x {pdfH}");

        var outPath = Path.Combine(_dir, "corners_rot90.pdf");
        var anns = new List<Annotation>();
        // Visual page is pdfW × pdfH (landscape 792×612). Place at X=100 and various Y in [0, pdfH].
        foreach (var y in new[] { 50.0, 150.0, 300.0, 450.0, 580.0 })
        {
            anns.Add(new TextAnnotation
            {
                X = 100, Y = y,
                WidthPt = 200, HeightPt = 18,
                Text = $"Y={y:F0}",
                FontFamily = "Helvetica",
                PageIndex = 0
            });
        }

        var pages = new[]
        {
            ((PageSource, int, double, double, IEnumerable<Annotation>))
            (new PageSource(bytes, 0), 0, pdfW, pdfH, (IEnumerable<Annotation>)anns)
        };

        var saveService = new PdfSaveService(new NullSvgRenderService(), NullLogger<PdfSaveService>.Instance);
        await saveService.SaveAsync(outPath, pages);

        var pngPath = Path.Combine(_dir, "corners_rot90.png");
        RenderAndSavePng(outPath, pngPath);
        _output.WriteLine($"Rendered: {pngPath}");
        _output.WriteLine($"Rotated page visual {pdfW}×{pdfH}. Each annotation at X=100, Y={{50,150,300,450,580}}.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Diagnostic_CornersForAllRotations(int sourceRotation)
    {
        byte[] bytes;
        using (var doc = new PdfDocument())
        {
            var p = doc.AddPage();
            p.Width = PdfSharp.Drawing.XUnit.FromPoint(612);
            p.Height = PdfSharp.Drawing.XUnit.FromPoint(792);
            p.Rotate = sourceRotation;
            using var ms = new MemoryStream();
            doc.Save(ms, closeStream: false);
            bytes = ms.ToArray();
        }

        var svc2 = new PdfRenderService();
        var (pdfW, pdfH) = svc2.GetPageSize(bytes, 0);

        var outPath = Path.Combine(_dir, $"rot{sourceRotation}.pdf");
        var anns = new List<Annotation>();
        // Place at 4 corners and center of the VISUAL view (pdfW × pdfH).
        var positions = new[]
        {
            (X: 50.0, Y: 50.0, label: "TL"),
            (X: pdfW - 150, Y: 50.0, label: "TR"),
            (X: 50.0, Y: pdfH - 40, label: "BL"),
            (X: pdfW - 150, Y: pdfH - 40, label: "BR"),
            (X: pdfW / 2 - 50, Y: pdfH / 2 - 9, label: "CENTER")
        };
        foreach (var (x, y, lbl) in positions)
        {
            anns.Add(new TextAnnotation
            {
                X = x, Y = y,
                WidthPt = 100, HeightPt = 18,
                Text = lbl,
                FontFamily = "Helvetica",
                PageIndex = 0
            });
        }

        var pages = new[]
        {
            ((PageSource, int, double, double, IEnumerable<Annotation>))
            (new PageSource(bytes, 0), 0, pdfW, pdfH, (IEnumerable<Annotation>)anns)
        };

        var saveService = new PdfSaveService(new NullSvgRenderService(), NullLogger<PdfSaveService>.Instance);
        await saveService.SaveAsync(outPath, pages);

        var pngPath = Path.Combine(_dir, $"rot{sourceRotation}.png");
        RenderAndSavePng(outPath, pngPath);
        _output.WriteLine($"Rendered rotation={sourceRotation} (visual {pdfW}x{pdfH}): {pngPath}");
    }

    [Fact]
    public async Task TextAnnotation_SourcePageHasIntrinsicRotate_DoesItLandCorrectly()
    {
        // Simulate a source PDF whose first page was saved with /Rotate 90 in its dictionary.
        // pdfium renders this rotated, so the user sees a landscape view. The app's
        // PageItem.RotationDegrees = 0 (user didn't rotate). Click at "X=300 Y=400" in the
        // visual (rotated) coords. Does save put the annotation at the right spot?
        byte[] bytes;
        using (var doc = new PdfDocument())
        {
            var p = doc.AddPage();
            p.Width = PdfSharp.Drawing.XUnit.FromPoint(612);
            p.Height = PdfSharp.Drawing.XUnit.FromPoint(792);
            p.Rotate = 90; // intrinsic PDF rotation
            using var ms = new MemoryStream();
            doc.Save(ms, closeStream: false);
            bytes = ms.ToArray();
        }

        // When pdfium renders a page with intrinsic Rotate=90, the returned dimensions
        // and bitmap are rotated. So the app's PageItem.OriginalWidthPt might be 792 or 612
        // depending on pdfium's behavior. Let's see what GetPageSize reports.
        var svc2 = new PdfRenderService();
        var (pdfW, pdfH) = svc2.GetPageSize(bytes, 0);
        _output.WriteLine($"pdfium reports page size as: {pdfW} x {pdfH} (source has MediaBox 612x792, /Rotate 90)");

        // Use the reported dimensions as the "original" dims that PageItem stores.
        var text = new TextAnnotation
        {
            X = 300, Y = 400,
            WidthPt = 50, HeightPt = 18,
            Text = "HELLO-ROT",
            FontFamily = "Helvetica",
            PageIndex = 0
        };

        var pages = new[]
        {
            ((PageSource, int, double, double, IEnumerable<Annotation>))
            (new PageSource(bytes, 0), 0, pdfW, pdfH, new Annotation[] { text })
        };

        var outPath = Path.Combine(_dir, "text_src_rotated.pdf");
        var svc = new PdfSaveService(new NullSvgRenderService(), NullLogger<PdfSaveService>.Instance);
        await svc.SaveAsync(outPath, pages);

        using var read = PdfReader.Open(outPath, PdfDocumentOpenMode.Modify);
        var outPage = read.Pages[0];
        _output.WriteLine($"Saved Page.Rotate = {outPage.Rotate}, Width={outPage.Width} Height={outPage.Height}");
        _output.WriteLine(ExtractContentStreams(outPage));

        var pngPath = Path.Combine(_dir, "text_src_rotated.png");
        RenderAndSavePng(outPath, pngPath);
        _output.WriteLine($"Rendered: {pngPath}");
    }

    [Fact]
    public async Task TextAnnotation_Rotated90_PdfContent()
    {
        var bytes = MakeBlankPdf();
        var outPath = Path.Combine(_dir, "text_rot90.pdf");

        // Rotated 90° CW: logical page is 792 wide × 612 tall (portrait becomes landscape view).
        // User places annotation at X=300 Y=400 in the rotated view (612 wide original → rotated W=792).
        var text = new TextAnnotation
        {
            X = 300, Y = 400,
            WidthPt = 50, HeightPt = 18,
            Text = "HELLO90",
            FontFamily = "Helvetica",
            PageIndex = 0
        };

        var pages = new[]
        {
            ((PageSource, int, double, double, IEnumerable<Annotation>))
            (new PageSource(bytes, 0), 90, 612.0, 792.0, new Annotation[] { text })
        };

        var svc = new PdfSaveService(new NullSvgRenderService(), NullLogger<PdfSaveService>.Instance);
        await svc.SaveAsync(outPath, pages);

        using var read = PdfReader.Open(outPath, PdfDocumentOpenMode.Modify);
        var outPage = read.Pages[0];
        _output.WriteLine($"Page.Rotate = {outPage.Rotate}, Page.Width={outPage.Width}, Page.Height={outPage.Height}");
        var content = ExtractContentStreams(outPage);
        _output.WriteLine("Rotated 90° — input X=300 Y=400 (rotated-view coords)");
        _output.WriteLine(content);

        var pngPath = Path.Combine(_dir, "text_rot90.png");
        RenderAndSavePng(outPath, pngPath);
        _output.WriteLine($"Rendered: {pngPath}");
    }

    [Fact]
    public async Task SvgAnnotation_PdfContent_ShowsCoordinates()
    {
        var bytes = MakeBlankPdf();
        var outPath = Path.Combine(_dir, "svg.pdf");
        var svgPath = Path.Combine(_dir, "sig.svg");
        File.WriteAllText(svgPath,
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 50\" width=\"100\" height=\"50\">\n" +
            "  <rect x=\"0\" y=\"0\" width=\"100\" height=\"50\" fill=\"black\"/>\n" +
            "</svg>\n");

        var svgAnn = new SvgAnnotation
        {
            X = 200, Y = 100,
            SvgFilePath = svgPath,
            Scale = 1.0,
            OriginalWidthPt = 75, OriginalHeightPt = 37.5,
            PageIndex = 0,
        };
        svgAnn.WidthPt = 75; svgAnn.HeightPt = 37.5;

        var svgSvc = new SvgRenderService(NullLogger<SvgRenderService>.Instance);
        var pages = new[]
        {
            ((PageSource, int, double, double, IEnumerable<Annotation>))
            (new PageSource(bytes, 0), 0, 612.0, 792.0, new Annotation[] { svgAnn })
        };

        var svc = new PdfSaveService(svgSvc, NullLogger<PdfSaveService>.Instance);
        await svc.SaveAsync(outPath, pages);

        Assert.True(File.Exists(outPath));

        using var read = PdfReader.Open(outPath, PdfDocumentOpenMode.Modify);
        var outPage = read.Pages[0];
        var content = ExtractContentStreams(outPage);
        _output.WriteLine("Input: X=200 Y=100 W=75 H=37.5 (GDI top-left origin), Page 612x792");
        _output.WriteLine($"Output file: {new FileInfo(outPath).Length} bytes");
        _output.WriteLine("--- content streams ---");
        _output.WriteLine(content);

        var pngPath = Path.Combine(_dir, "svg.png");
        RenderAndSavePng(outPath, pngPath);
        _output.WriteLine($"Rendered PNG: {pngPath}");
    }

    private static string ExtractContentStreams(PdfPage page)
    {
        var sb = new StringBuilder();
        // CreateSingleContent merges/dereferences all content streams into one.
        var content = page.Contents.CreateSingleContent();
        var bytes = content.Stream.UnfilteredValue;
        sb.AppendLine($"Combined content: {bytes.Length} bytes");
        var chars = new char[Math.Min(4000, bytes.Length)];
        for (int j = 0; j < chars.Length; j++)
            chars[j] = (bytes[j] >= 32 && bytes[j] < 127) || bytes[j] == '\n' || bytes[j] == '\r'
                ? (char)bytes[j] : '.';
        sb.AppendLine(new string(chars));
        return sb.ToString();
    }

    private sealed class NullSvgRenderService : ISvgRenderService
    {
        public (double WidthPt, double HeightPt) GetSvgSize(string svgPath) => (0, 0);
        public (double WidthPt, double HeightPt) GetImageSize(string path) => (0, 0);
        public Avalonia.Media.Imaging.Bitmap RenderForDisplay(string svgPath, double scale, int renderDpi) =>
            throw new NotSupportedException();
        public (double WidthPt, double HeightPt, Avalonia.Media.Imaging.Bitmap Bitmap) GetSizeAndRenderForDisplay(
            string svgPath, double scale, int renderDpi) => throw new NotSupportedException();
        public Avalonia.Media.Imaging.Bitmap ResampleForDisplay(string path, double widthPt, double heightPt, int dpi) =>
            throw new NotSupportedException();
        public byte[] RenderToVectorPdf(string svgPath, double scale) => Array.Empty<byte>();
        public Avalonia.Media.Imaging.Bitmap RenderForAnnotation(SvgAnnotation annotation, int dpi) =>
            throw new NotSupportedException();
    }
}
