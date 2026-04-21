using Avalonia.Media;
using Avalonia.Media.Imaging;
using pdfSignr.Models;
using pdfSignr.Services;
using pdfSignr.ViewModels;

namespace pdfSignr.Tests;

/// <summary>
/// Stubs that let a MainViewModel be constructed without any real PDF rendering, file
/// dialogs, or dispatchers. Tests drive the VM directly and inspect state.
/// </summary>
internal static class TestHarness
{
    public static MainViewModel CreateViewModel(
        IFileDialogService? fileDialogs = null,
        IDialogService? dialogs = null)
    {
        var settings = new StubSettings();
        // TextAnnotation.FontSize touches the static catalog during UpdateStatusText. Set it
        // once per process so tests can SelectAnnotation without tripping the guard.
        TextAnnotation.Catalog ??= new StubFontCatalog();
        return new MainViewModel(
            fileDialogs ?? new StubFileDialogs(),
            new StubRenderService(),
            new StubSaveService(),
            new StubCompressService(),
            new StubFontCatalog(),
            new StubSvgRenderer(),
            dialogs ?? new StubDialogs(),
            new ViewportViewModel(),
            new UndoRedoService(settings),
            new AppClipboardService());
    }

    /// <summary>Attaches a page to the VM with a known PDF-point size and optional rotation.</summary>
    public static PageItem AddPage(MainViewModel vm, double widthPt = 600, double heightPt = 800, int rotationDegrees = 0)
    {
        var page = new PageItem
        {
            Index = vm.Pages.Count,
            OriginalWidthPt = widthPt,
            OriginalHeightPt = heightPt,
            RotationDegrees = rotationDegrees,
            Source = new PageSource(Array.Empty<byte>(), 0),
            ParentVM = vm,
            DisplayNumber = vm.Pages.Count + 1,
            IsFirst = vm.Pages.Count == 0,
        };
        vm.Pages.Add(page);
        return page;
    }

    public sealed class StubSettings : ISettingsService
    {
        public AppSettings Current { get; private set; } = new();
        public event Action<AppSettings>? Changed;
        public Task SaveAsync() => Task.CompletedTask;
        public void Update(Func<AppSettings, AppSettings> mutate)
        {
            Current = mutate(Current);
            Changed?.Invoke(Current);
        }
    }

    public sealed class StubFileDialogs : IFileDialogService
    {
        public string? NextOpenFile { get; set; }
        public string? NextSaveFile { get; set; }
        public bool ConfirmReturn { get; set; } = true;
        public List<(string Title, string Message)> Errors { get; } = new();

        public Task<string?> PickOpenFileAsync(string title, string[] patterns) => Task.FromResult(NextOpenFile);
        public Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension, string[] patterns)
            => Task.FromResult(NextSaveFile);
        public Task ShowErrorAsync(string title, string message) { Errors.Add((title, message)); return Task.CompletedTask; }
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(ConfirmReturn);
    }

    public sealed class StubDialogs : IDialogService
    {
        public string? PasswordReturn { get; set; }
        public Task<string?> ShowPasswordAsync(string title, string message, bool showError) => Task.FromResult(PasswordReturn);
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
    }

    public sealed class StubFontCatalog : IFontCatalog
    {
        public IReadOnlyList<string> PdfFontNames { get; } = new[] { "Helvetica", "Times-Roman", "Courier" };
        public string MapToFaceName(string pdfFont) => pdfFont;
        public string GetAvaloniaFontUri(string pdfFont) => pdfFont;
        public FontFamily GetMeasureFontFamily(string pdfFont) => new("Sans Serif");
    }

    public sealed class StubRenderService : IPdfRenderService
    {
        public int GetPageCount(byte[] pdfBytes, string? password = null) => 0;
        public (double WidthPt, double HeightPt) GetPageSize(byte[] pdfBytes, int pageIndex, string? password = null) => (612, 792);
        public IReadOnlyList<(double WidthPt, double HeightPt)> GetAllPageSizes(byte[] pdfBytes, string? password = null)
            => Array.Empty<(double, double)>();
        public Bitmap RenderPage(byte[] pdfBytes, int pageIndex, int dpi, int rotationDegrees = 0, string? password = null)
            => throw new NotSupportedException();
    }

    public sealed class StubSaveService : IPdfSaveService
    {
        public Task<SaveResult> SaveAsync(
            string outputPath,
            IEnumerable<(PageSource Source, int RotationDegrees, IEnumerable<Annotation> Annotations)> pages,
            string? outputPassword = null, IProgress<int>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new SaveResult(0, 0));
    }

    public sealed class StubCompressService : IPdfCompressService
    {
        public Task<CompressResult> CompressAsync(IReadOnlyList<(PageSource Source, int RotationDegrees)> pageSources,
            string outputPath, CompressionPreset preset, string? outputPassword = null,
            IProgress<int>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new CompressResult(0, 0, 0, 0));

        public Task<CompressResult> RasterizeAsync(IReadOnlyList<(PageSource Source, int RotationDegrees)> pageSources,
            string outputPath, CompressionPreset preset, string? outputPassword = null,
            IProgress<int>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new CompressResult(0, 0, 0, 0));
    }

    public sealed class StubSvgRenderer : ISvgRenderService
    {
        public (double WidthPt, double HeightPt) GetSvgSize(string svgPath) => (0, 0);
        public (double WidthPt, double HeightPt) GetImageSize(string path) => (0, 0);
        public Bitmap RenderForDisplay(string svgPath, double scale, int renderDpi) => throw new NotSupportedException();
        public (double WidthPt, double HeightPt, Bitmap Bitmap) GetSizeAndRenderForDisplay(string svgPath, double scale, int renderDpi)
            => throw new NotSupportedException();
        public Bitmap ResampleForDisplay(string path, double widthPt, double heightPt, int dpi) => throw new NotSupportedException();
        public byte[] RenderToVectorPdf(string svgPath, double scale) => Array.Empty<byte>();
        public Bitmap RenderForAnnotation(SvgAnnotation annotation, int dpi) => throw new NotSupportedException();
    }
}
