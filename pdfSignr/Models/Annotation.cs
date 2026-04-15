using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using pdfSignr.Services;

namespace pdfSignr.Models;

public abstract partial class Annotation : ObservableObject
{
    [ObservableProperty] private double _x;          // PDF points from left edge
    [ObservableProperty] private double _y;          // PDF points from top edge
    [ObservableProperty] private int _pageIndex;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private double _rotation;   // degrees, clockwise

    public abstract double WidthPt { get; set; }
    public abstract double HeightPt { get; set; }
}

public partial class TextAnnotation : Annotation
{
    [ObservableProperty] private string _text = "Text";
    [ObservableProperty] private string _fontFamily = "Helvetica";
    private double _widthPt = 40;
    private double _heightPt = 18;
    private double? _cachedFontSize;

    // Font size is derived from bounding box height (cached until inputs change)
    public double FontSize => _cachedFontSize ??= ComputeFontSize();

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(FontSize));
    partial void OnFontFamilyChanged(string value)
    {
        _cachedFontSize = null;
        OnPropertyChanged(nameof(FontSize));
    }

    public override double WidthPt
    {
        get => _widthPt;
        set => SetProperty(ref _widthPt, value);
    }

    public override double HeightPt
    {
        get => _heightPt;
        set { if (SetProperty(ref _heightPt, value)) { _cachedFontSize = null; OnPropertyChanged(nameof(FontSize)); } }
    }

    // Shared cache — also used by PageCanvas.MakeFormattedText
    internal static readonly Dictionary<string, Typeface> TypefaceCache = new();

    private double ComputeFontSize()
    {
        if (_heightPt <= 0) return 12;
        // Measure text at a reference size to find the ratio
        const double refSize = 72.0;
        double dpiScale = PdfConstants.DpiScale;
        if (!TypefaceCache.TryGetValue(FontFamily, out var typeface))
        {
            typeface = new Typeface(MapFontForMeasure(FontFamily));
            TypefaceCache[FontFamily] = typeface;
        }
        var ft = new FormattedText("Xg", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, refSize * dpiScale, Brushes.Black);
        double measuredHeightPt = ft.Height / dpiScale;
        if (measuredHeightPt <= 0) return _heightPt;
        return refSize * _heightPt / measuredHeightPt;
    }

    internal static FontFamily MapFontForMeasure(string pdfFont)
        => new(FontResolver.GetAvaloniaFontUri(pdfFont));
}

/// <summary>Annotation backed by an external file (SVG vector or raster PNG/JPG).</summary>
public partial class SvgAnnotation : Annotation, IDisposable
{
    [ObservableProperty] private string _svgFilePath = "";
    [ObservableProperty] private double _scale = 1.0;
    [ObservableProperty] private double _originalWidthPt;
    [ObservableProperty] private double _originalHeightPt;
    private double _svgWidthPt;
    private double _svgHeightPt;

    public bool IsRaster { get; init; }
    public Bitmap? RenderedBitmap { get; set; }
    public int RenderedDpi { get; set; }

    public override double WidthPt
    {
        get => _svgWidthPt;
        set => SetProperty(ref _svgWidthPt, value);
    }

    public override double HeightPt
    {
        get => _svgHeightPt;
        set => SetProperty(ref _svgHeightPt, value);
    }

    /// <summary>Stores a new rendered bitmap and disposes the old one.</summary>
    public void ReplaceRenderedBitmap(Bitmap newBitmap, int dpi)
    {
        var old = RenderedBitmap;
        RenderedBitmap = newBitmap;
        RenderedDpi = dpi;
        old?.Dispose();
    }

    /// <summary>Re-renders the display bitmap at the given DPI.</summary>
    public void ReRender(int dpi)
    {
        var bitmap = IsRaster
            ? SvgRenderService.ResampleForDisplay(SvgFilePath, WidthPt, HeightPt, dpi)
            : SvgRenderService.RenderForDisplay(SvgFilePath, Scale, dpi);
        ReplaceRenderedBitmap(bitmap, dpi);
    }

    public void Dispose()
    {
        RenderedBitmap?.Dispose();
        RenderedBitmap = null;
    }
}
