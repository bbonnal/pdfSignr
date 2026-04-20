using pdfSignr.Models;

namespace pdfSignr.Services;

public record SaveResult(int PagesWritten, long OutputSize);

public interface IPdfSaveService
{
    Task<SaveResult> SaveAsync(
        string outputPath,
        IEnumerable<(PageSource Source, int RotationDegrees, IEnumerable<Annotation> Annotations)> pages,
        string? outputPassword = null,
        IProgress<int>? progress = null,
        CancellationToken ct = default);
}
