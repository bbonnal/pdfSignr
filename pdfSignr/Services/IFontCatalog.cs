using Avalonia.Media;

namespace pdfSignr.Services;

/// <summary>
/// Catalog of PDF fonts bundled with the app. Provides name lists for UI pickers
/// and PDF-name → face/URI mappings for rendering and measurement.
/// </summary>
public interface IFontCatalog
{
    IReadOnlyList<string> PdfFontNames { get; }
    string MapToFaceName(string pdfFont);
    string GetAvaloniaFontUri(string pdfFont);
    FontFamily GetMeasureFontFamily(string pdfFont);
}
