using Avalonia.Media;

namespace pdfSignr.Services;

public class FontCatalog : IFontCatalog
{
    private static readonly string[] Names = ["Helvetica", "Times-Roman", "Courier"];

    private static readonly Dictionary<string, string> AvaloniaFontUris = new()
    {
        ["Helvetica"] = "avares://pdfSignr/Assets/Fonts/LiberationSans-Regular.ttf#Liberation Sans",
        ["Times-Roman"] = "avares://pdfSignr/Assets/Fonts/LiberationSerif-Regular.ttf#Liberation Serif",
        ["Courier"] = "avares://pdfSignr/Assets/Fonts/LiberationMono-Regular.ttf#Liberation Mono",
    };

    public IReadOnlyList<string> PdfFontNames => Names;

    public string MapToFaceName(string pdfFont) => pdfFont switch
    {
        "Helvetica" => "LiberationSans",
        "Times-Roman" or "Times" => "LiberationSerif",
        "Courier" => "LiberationMono",
        _ => "LiberationSans"
    };

    public string GetAvaloniaFontUri(string pdfFont)
        => AvaloniaFontUris.GetValueOrDefault(pdfFont, AvaloniaFontUris["Helvetica"]);

    public FontFamily GetMeasureFontFamily(string pdfFont)
        => new(GetAvaloniaFontUri(pdfFont));
}
