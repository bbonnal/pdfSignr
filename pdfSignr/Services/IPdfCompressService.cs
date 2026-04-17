using pdfSignr.Models;

namespace pdfSignr.Services;

public interface IPdfCompressService
{
    Task<CompressResult> CompressAsync(
        IReadOnlyList<(PageSource Source, int RotationDegrees)> pageSources,
        string outputPath,
        CompressionPreset preset,
        string? outputPassword = null,
        IProgress<int>? progress = null,
        CancellationToken ct = default);

    Task<CompressResult> RasterizeAsync(
        IReadOnlyList<(PageSource Source, int RotationDegrees)> pageSources,
        string outputPath,
        CompressionPreset preset,
        string? outputPassword = null,
        IProgress<int>? progress = null,
        CancellationToken ct = default);
}
