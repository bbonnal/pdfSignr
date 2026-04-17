using pdfSignr.Models;

namespace pdfSignr.Services;

public interface IPdfSaveService
{
    void Save(
        string outputPath,
        IEnumerable<(PageSource Source, int RotationDegrees, double OriginalWidthPt, double OriginalHeightPt,
            IEnumerable<Annotation> Annotations)> pages,
        string? outputPassword = null);
}
