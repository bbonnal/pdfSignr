using System.Collections.Concurrent;
using System.Reflection;
using PdfSharp.Fonts;

namespace pdfSignr.Services;

/// <summary>
/// Font resolver for PDFsharp that uses bundled Liberation fonts.
/// Works identically on Windows, Linux, and macOS — no system font dependency.
/// </summary>
public class FontResolver : IFontResolver
{
    private readonly IFontCatalog _catalog;

    public FontResolver(IFontCatalog catalog)
    {
        _catalog = catalog;
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        => new(_catalog.MapToFaceName(familyName));

    public byte[]? GetFont(string faceName)
    {
        if (FontCache.TryGetValue(faceName, out var cached))
            return cached;

        if (!FontResourceMap.TryGetValue(faceName, out var resourceName))
            return null;

        using var stream = ThisAssembly.GetManifestResourceStream(resourceName);
        if (stream == null) return null;

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var data = ms.ToArray();
        return FontCache.GetOrAdd(faceName, data);
    }

    private static readonly Assembly ThisAssembly = Assembly.GetExecutingAssembly();
    private static readonly ConcurrentDictionary<string, byte[]> FontCache = new();

    private static readonly Dictionary<string, string> FontResourceMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LiberationSans"] = "pdfSignr.Assets.Fonts.LiberationSans-Regular.ttf",
        ["LiberationSerif"] = "pdfSignr.Assets.Fonts.LiberationSerif-Regular.ttf",
        ["LiberationMono"] = "pdfSignr.Assets.Fonts.LiberationMono-Regular.ttf",
    };
}
