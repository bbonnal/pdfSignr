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

    private const string BlackRectSvgContent =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 50\" width=\"100\" height=\"50\">" +
        "<rect x=\"0\" y=\"0\" width=\"100\" height=\"50\" fill=\"black\"/></svg>";

    private static string WriteBlackRectSvg(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, BlackRectSvgContent);
        return path;
    }

    /// <summary>
    /// Returns the bounding box of dark pixels (Red &lt; 80) in the rendered bitmap, in PDF points
    /// (top-left origin). Returns null if no dark pixels are found.
    /// </summary>
    private static (double Left, double Top, double Right, double Bottom)? FindDarkBboxPt(
        SkiaSharp.SKBitmap bitmap, int dpi, byte darknessThreshold = 80)
    {
        double ptToPx = dpi / 72.0;
        int w = bitmap.Width, h = bitmap.Height;
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (bitmap.GetPixel(x, y).Red < darknessThreshold)
                {
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
        if (maxX < 0) return null;
        return (minX / ptToPx, minY / ptToPx, maxX / ptToPx, maxY / ptToPx);
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
            ((PageSource, int, IEnumerable<Annotation>))
            (new PageSource(bytes, 0), 0, new Annotation[] { text })
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
            ((PageSource, int, IEnumerable<Annotation>))
            (new PageSource(bytes, 0), 0, (IEnumerable<Annotation>)anns)
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
            ((PageSource, int, IEnumerable<Annotation>))
            (new PageSource(bytes, 0), 0, (IEnumerable<Annotation>)anns)
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
            ((PageSource, int, IEnumerable<Annotation>))
            (new PageSource(bytes, 0), 0, (IEnumerable<Annotation>)anns)
        };

        var saveService = new PdfSaveService(new NullSvgRenderService(), NullLogger<PdfSaveService>.Instance);
        await saveService.SaveAsync(outPath, pages);

        var pngPath = Path.Combine(_dir, $"rot{sourceRotation}.png");
        RenderAndSavePng(outPath, pngPath);
        _output.WriteLine($"Rendered rotation={sourceRotation} (visual {pdfW}x{pdfH}): {pngPath}");
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(0, 90)] [InlineData(0, 180)] [InlineData(0, 270)]
    [InlineData(90, 0)] [InlineData(90, 90)] [InlineData(90, 180)] [InlineData(90, 270)]
    [InlineData(180, 0)] [InlineData(180, 90)] [InlineData(180, 180)] [InlineData(180, 270)]
    [InlineData(270, 0)] [InlineData(270, 90)] [InlineData(270, 180)] [InlineData(270, 270)]
    public async Task Compound_SourceAndUserRotation_AnnotationsStayOnPage(int sourceRotation, int userRotation)
    {
        // Build a source page with the given intrinsic /Rotate.
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

        // pdfium dims reflect the intrinsic rotation.
        var (pdfW, pdfH) = new PdfRenderService().GetPageSize(bytes, 0);

        // After the user rotates by userRotation, the visual view swaps when user is 90/270.
        bool userSwapsAxes = userRotation is 90 or 270;
        double visualW = userSwapsAxes ? pdfH : pdfW;
        double visualH = userSwapsAxes ? pdfW : pdfH;

        // Place a marker at each corner of the visual view — this is the coord space the user
        // sees and places annotations in.
        const double boxW = 100, boxH = 22;
        var positions = new[]
        {
            (X: 20.0, Y: 20.0, label: "TL"),
            (X: visualW - boxW - 20, Y: 20.0, label: "TR"),
            (X: 20.0, Y: visualH - boxH - 20, label: "BL"),
            (X: visualW - boxW - 20, Y: visualH - boxH - 20, label: "BR"),
        };
        var anns = positions.Select(p => (Annotation)new TextAnnotation
        {
            X = p.X, Y = p.Y, WidthPt = boxW, HeightPt = boxH,
            Text = p.label, FontFamily = "Helvetica", PageIndex = 0
        }).ToList();

        var outPath = Path.Combine(_dir, $"compound_s{sourceRotation}_u{userRotation}.pdf");
        var pages = new[]
        {
            ((PageSource, int, IEnumerable<Annotation>))
            (new PageSource(bytes, 0), userRotation, (IEnumerable<Annotation>)anns)
        };

        var svc = new PdfSaveService(new NullSvgRenderService(), NullLogger<PdfSaveService>.Instance);
        await svc.SaveAsync(outPath, pages);

        var pngPath = Path.Combine(_dir, $"compound_s{sourceRotation}_u{userRotation}.png");
        RenderAndSavePng(outPath, pngPath);
        _output.WriteLine($"source={sourceRotation} user={userRotation} visual={visualW}x{visualH}: {pngPath}");

        // Pixel-presence assertions: each corner label should produce darkness in its
        // expected quadrant of the rendered PNG. This catches any transform that would
        // push annotations off-page or into the wrong corner.
        using var pngStream = File.OpenRead(pngPath);
        using var skBitmap = SkiaSharp.SKBitmap.Decode(pngStream);
        int w = skBitmap.Width, h = skBitmap.Height;
        int halfW = w / 2, halfH = h / 2;

        bool QuadrantHasDarkPixels(int x0, int y0, int x1, int y1)
        {
            int darkCount = 0;
            for (int y = y0; y < y1; y += 2)
                for (int x = x0; x < x1; x += 2)
                    if (skBitmap.GetPixel(x, y).Red < 128) darkCount++;
            return darkCount > 10;
        }

        Assert.True(QuadrantHasDarkPixels(0, 0, halfW, halfH),
            $"TL missing in top-left quadrant (source={sourceRotation}, user={userRotation})");
        Assert.True(QuadrantHasDarkPixels(halfW, 0, w, halfH),
            $"TR missing in top-right quadrant (source={sourceRotation}, user={userRotation})");
        Assert.True(QuadrantHasDarkPixels(0, halfH, halfW, h),
            $"BL missing in bottom-left quadrant (source={sourceRotation}, user={userRotation})");
        Assert.True(QuadrantHasDarkPixels(halfW, halfH, w, h),
            $"BR missing in bottom-right quadrant (source={sourceRotation}, user={userRotation})");
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
            ((PageSource, int, IEnumerable<Annotation>))
            (new PageSource(bytes, 0), 0, new Annotation[] { text })
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
            ((PageSource, int, IEnumerable<Annotation>))
            (new PageSource(bytes, 0), 90, new Annotation[] { text })
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
    public async Task UiAndPdfText_BaselineAgreesWithinOnePoint()
    {
        // Invariant we want: drawing a TextAnnotation at (X, Y) in the UI, and saving the
        // same annotation to PDF, should place the glyph baseline at the same Y coordinate
        // modulo font metrics rounding. Avalonia's FormattedText is the UI's source of
        // truth; the saved PDF must match its Baseline property.
        const double annX = 150, annY = 300, annW = 200, annH = 40;
        var ann = new TextAnnotation
        {
            X = annX, Y = annY, WidthPt = annW, HeightPt = annH,
            Text = "Baseline", FontFamily = "Helvetica", PageIndex = 0
        };

        // UI baseline (ground truth): Avalonia FormattedText.Baseline is in DIPs at the
        // FormattedText's font size (which is scaled by DpiScale for display). Convert
        // back to PDF points to compare against the PDF output.
        var typeface = new Avalonia.Media.Typeface(TextAnnotation.MapFontForMeasure(ann.FontFamily));
        var ft = new Avalonia.Media.FormattedText(
            ann.Text, System.Globalization.CultureInfo.InvariantCulture,
            Avalonia.Media.FlowDirection.LeftToRight, typeface,
            ann.FontSize * pdfSignr.Models.PdfConstants.DpiScale, Avalonia.Media.Brushes.Black);
        double uiBaselinePt = ft.Baseline / pdfSignr.Models.PdfConstants.DpiScale;
        double expectedBaselineAbsY = annY + uiBaselinePt;

        // Render the saved PDF and find the darkest row within a tight window around the
        // expected baseline. The text's baseline sits roughly at the bottom of most glyphs,
        // so we look for the last dark row the text spans.
        var bytes = MakeBlankPdf();
        var outPath = Path.Combine(_dir, "baseline.pdf");
        var pages = new[]
        {
            ((PageSource, int, IEnumerable<Annotation>))
            (new PageSource(bytes, 0), 0, new Annotation[] { ann })
        };
        var svc = new PdfSaveService(new NullSvgRenderService(), NullLogger<PdfSaveService>.Instance);
        await svc.SaveAsync(outPath, pages);

        const int dpi = 144;
        using var skBitmap = PDFtoImage.Conversion.ToImage(
            File.ReadAllBytes(outPath), 0, null, new PDFtoImage.RenderOptions(Dpi: dpi));
        double ptToPx = dpi / 72.0;
        int bitmapW = skBitmap.Width;

        int FindTextBottomRow(int y0, int y1, int x0, int x1)
        {
            int bottom = -1;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    if (skBitmap.GetPixel(x, y).Red < 128) { bottom = y; break; }
                }
            }
            return bottom;
        }

        int searchTop = (int)(annY * ptToPx) - 4;
        int searchBottom = (int)((annY + annH) * ptToPx) + 4;
        int searchLeft = (int)(annX * ptToPx) - 4;
        int searchRight = (int)((annX + annW) * ptToPx) + 4;
        int glyphBottomPx = FindTextBottomRow(searchTop, searchBottom, searchLeft, searchRight);
        Assert.True(glyphBottomPx > 0, "No dark pixels found for 'Baseline' text in saved PDF");

        double glyphBottomPt = glyphBottomPx / ptToPx;
        double delta = glyphBottomPt - expectedBaselineAbsY;
        _output.WriteLine($"UI baseline={expectedBaselineAbsY:F2}pt, PDF glyph bottom={glyphBottomPt:F2}pt, Δ={delta:F2}pt");
        // Tolerance: descenders can extend a few points below baseline (the "e" in "Baseline"
        // has no descender; "B/a/s/l/i/n" stop at the baseline). Typography descent for "e"
        // at ~16pt is <1pt, so tolerate ±3pt to absorb font-metric rounding differences.
        Assert.True(Math.Abs(delta) <= 3.0, $"UI vs PDF baseline mismatch: Δ={delta:F2}pt");
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
            ((PageSource, int, IEnumerable<Annotation>))
            (new PageSource(bytes, 0), 0, new Annotation[] { svgAnn })
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

    [Fact]
    public async Task TextAnnotation_SourcePageHasNonZeroMediaBoxOrigin()
    {
        // Real-world PDFs (scans, imposition layouts) often ship with a MediaBox whose
        // origin is NOT (0,0). Assert that an annotation placed at (X, Y) still lands at
        // visual (X, Y) on the rendered page in that case — otherwise annotations drift
        // by the MediaBox offset, which matches the user's "left and down" symptom for
        // PDFs whose MediaBox is shifted up-right of (0,0).
        byte[] bytes;
        using (var doc = new PdfDocument())
        {
            var p = doc.AddPage();
            // MediaBox origin at (50, 50), content 612×792.
            p.MediaBox = new PdfSharp.Pdf.PdfRectangle(
                new PdfSharp.Drawing.XPoint(50, 50),
                new PdfSharp.Drawing.XPoint(50 + 612, 50 + 792));
            using var ms = new MemoryStream();
            doc.Save(ms, closeStream: false);
            bytes = ms.ToArray();
        }

        var (pdfW, pdfH) = new PdfRenderService().GetPageSize(bytes, 0);
        _output.WriteLine($"Source MediaBox [50 50 662 842], pdfium reports: {pdfW}x{pdfH}");

        const double annX = 150, annY = 200, annW = 120, annH = 60;
        var svgAnn = new SvgAnnotation
        {
            X = annX, Y = annY, WidthPt = annW, HeightPt = annH,
            SvgFilePath = WriteBlackRectSvg(_dir, "mb_sig.svg"), Scale = 1.0,
            OriginalWidthPt = 75, OriginalHeightPt = 37.5, PageIndex = 0,
        };

        var outPath = Path.Combine(_dir, "mediabox_offset.pdf");
        var pages = new[]
        {
            ((PageSource, int, IEnumerable<Annotation>))
            (new PageSource(bytes, 0), 0, new Annotation[] { svgAnn })
        };
        var saveService = new PdfSaveService(new SvgRenderService(NullLogger<SvgRenderService>.Instance),
            NullLogger<PdfSaveService>.Instance);
        await saveService.SaveAsync(outPath, pages);

        const int dpi = 144;
        using var skBitmap = PDFtoImage.Conversion.ToImage(
            File.ReadAllBytes(outPath), 0, null, new PDFtoImage.RenderOptions(Dpi: dpi));
        var bbox = FindDarkBboxPt(skBitmap, dpi);
        Assert.NotNull(bbox);
        _output.WriteLine($"SVG rect rendered at pt: L={bbox!.Value.Left:F2} T={bbox.Value.Top:F2} (expected L={annX} T={annY})");
        Assert.InRange(bbox.Value.Left, annX - 2, annX + 2);
        Assert.InRange(bbox.Value.Top, annY - 2, annY + 2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task MediaBoxOffset_Combined_WithSourceRotation(int sourceRotation)
    {
        // MediaBox offset combined with /Rotate was the worst case: in the broken build,
        // annotations landed outside the page on rotated pages. Verify the compensation
        // composes correctly with ApplyVisualTransform across all rotations.
        byte[] bytes;
        using (var doc = new PdfDocument())
        {
            var p = doc.AddPage();
            p.MediaBox = new PdfSharp.Pdf.PdfRectangle(
                new PdfSharp.Drawing.XPoint(30, 40),
                new PdfSharp.Drawing.XPoint(30 + 612, 40 + 792));
            p.Rotate = sourceRotation;
            using var ms = new MemoryStream();
            doc.Save(ms, closeStream: false);
            bytes = ms.ToArray();
        }

        var (pdfW, pdfH) = new PdfRenderService().GetPageSize(bytes, 0);

        const double annX = 100, annY = 150, annW = 80, annH = 40;
        var svgAnn = new SvgAnnotation
        {
            X = annX, Y = annY, WidthPt = annW, HeightPt = annH,
            SvgFilePath = WriteBlackRectSvg(_dir, $"mbrot_{sourceRotation}.svg"), Scale = 1.0,
            OriginalWidthPt = 75, OriginalHeightPt = 37.5, PageIndex = 0,
        };

        var outPath = Path.Combine(_dir, $"mbrot_{sourceRotation}.pdf");
        var pages = new[]
        {
            ((PageSource, int, IEnumerable<Annotation>))
            (new PageSource(bytes, 0), 0, new Annotation[] { svgAnn })
        };
        var svc = new PdfSaveService(new SvgRenderService(NullLogger<SvgRenderService>.Instance),
            NullLogger<PdfSaveService>.Instance);
        await svc.SaveAsync(outPath, pages);

        const int dpi = 144;
        using var skBitmap = PDFtoImage.Conversion.ToImage(
            File.ReadAllBytes(outPath), 0, null, new PDFtoImage.RenderOptions(Dpi: dpi));
        var bbox = FindDarkBboxPt(skBitmap, dpi);
        Assert.NotNull(bbox);
        _output.WriteLine($"rot={sourceRotation}: rendered TL=({bbox!.Value.Left:F2}, {bbox.Value.Top:F2}) expected=({annX}, {annY})");
        Assert.InRange(bbox.Value.Left, annX - 2, annX + 2);
        Assert.InRange(bbox.Value.Top, annY - 2, annY + 2);
    }

    [Fact]
    public async Task SvgAnnotation_RendersAtExactPosition()
    {
        // A solid-black SVG rect placed at a known (X, Y, W, H) must render in the saved
        // PDF with a dark bounding box matching (X, Y) within a few points. This closes
        // the "left and down" claim for signatures/SVGs specifically.
        var bytes = MakeBlankPdf();
        var outPath = Path.Combine(_dir, "svg_exact.pdf");

        const double annX = 180, annY = 240, annW = 120, annH = 60;
        var svgAnn = new SvgAnnotation
        {
            X = annX, Y = annY, WidthPt = annW, HeightPt = annH,
            SvgFilePath = WriteBlackRectSvg(_dir, "sig_exact.svg"),
            Scale = 1.0,
            OriginalWidthPt = 75, OriginalHeightPt = 37.5,
            PageIndex = 0,
        };

        var pages = new[]
        {
            ((PageSource, int, IEnumerable<Annotation>))
            (new PageSource(bytes, 0), 0, new Annotation[] { svgAnn })
        };
        var svc = new PdfSaveService(new SvgRenderService(NullLogger<SvgRenderService>.Instance),
            NullLogger<PdfSaveService>.Instance);
        await svc.SaveAsync(outPath, pages);

        const int dpi = 144;
        using var skBitmap = PDFtoImage.Conversion.ToImage(
            File.ReadAllBytes(outPath), 0, null, new PDFtoImage.RenderOptions(Dpi: dpi));
        var bbox = FindDarkBboxPt(skBitmap, dpi);
        Assert.NotNull(bbox);
        var (l, t, r, b) = bbox!.Value;
        _output.WriteLine($"SVG rect rendered at pt: L={l:F2} T={t:F2} R={r:F2} B={b:F2} " +
                          $"(expected: L={annX} T={annY} R={annX + annW} B={annY + annH})");
        Assert.InRange(l, annX - 2, annX + 2);
        Assert.InRange(t, annY - 2, annY + 2);
        Assert.InRange(r, annX + annW - 2, annX + annW + 2);
        Assert.InRange(b, annY + annH - 2, annY + annH + 2);
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
