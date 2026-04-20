using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using pdfSignr.Services;

namespace pdfSignr.Models;

public abstract partial class Annotation : ObservableObject, IDisposable
{
    [ObservableProperty] private double _x;          // PDF points from left edge
    [ObservableProperty] private double _y;          // PDF points from top edge
    [ObservableProperty] private int _pageIndex;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private double _rotation;   // degrees, clockwise

    public abstract double WidthPt { get; set; }
    public abstract double HeightPt { get; set; }

    /// <summary>Deep copy of this annotation, unselected, ready to be added to a page.</summary>
    public abstract Annotation Clone();

    public virtual void Dispose() { }
}

public partial class TextAnnotation : Annotation
{
    // Static by necessity: Models cannot hold injected services. Set once in App.Initialize from the DI container.
    internal static IFontCatalog? Catalog;

    [ObservableProperty] private string _text = "Text";
    [ObservableProperty] private string _fontFamily = "";
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
    internal static readonly ConcurrentDictionary<string, Typeface> TypefaceCache = new();

    private double ComputeFontSize()
    {
        if (_heightPt <= 0) return 12;
        if (string.IsNullOrEmpty(FontFamily)) return _heightPt;
        if (Catalog == null)
            throw new InvalidOperationException($"{nameof(TextAnnotation)}.{nameof(Catalog)} not initialized");
        const double refSize = 72.0;
        double dpiScale = PdfConstants.DpiScale;
        var typeface = TypefaceCache.GetOrAdd(FontFamily,
            ff => new Typeface(Catalog.GetMeasureFontFamily(ff)));
        var ft = new FormattedText("Xg", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, refSize * dpiScale, Brushes.Black);
        double measuredHeightPt = ft.Height / dpiScale;
        if (measuredHeightPt <= 0) return _heightPt;
        return refSize * _heightPt / measuredHeightPt;
    }

    internal static FontFamily MapFontForMeasure(string pdfFont)
    {
        if (Catalog != null) return Catalog.GetMeasureFontFamily(pdfFont);
        return new FontFamily("Sans Serif");
    }

    public override Annotation Clone() => new TextAnnotation
    {
        X = X, Y = Y, Rotation = Rotation, PageIndex = PageIndex,
        Text = Text, FontFamily = FontFamily,
        WidthPt = WidthPt, HeightPt = HeightPt,
    };
}

/// <summary>Annotation backed by an external file (SVG vector or raster PNG/JPG).</summary>
public partial class SvgAnnotation : Annotation
{
    // Static by necessity: Models cannot hold injected services. Set once in App.Initialize.
    internal static ISvgRenderService? Renderer;

    [ObservableProperty] private string _svgFilePath = "";
    [ObservableProperty] private double _scale = 1.0;
    [ObservableProperty] private double _originalWidthPt;
    [ObservableProperty] private double _originalHeightPt;
    private double _svgWidthPt;
    private double _svgHeightPt;

    public bool IsRaster { get; init; }

    private Bitmap? _renderedBitmap;
    public Bitmap? RenderedBitmap
    {
        get => _renderedBitmap;
        set => SetProperty(ref _renderedBitmap, value);
    }

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

    /// <summary>Re-renders the display bitmap at the given DPI. Call from any thread; swap is UI-safe.</summary>
    public void ReRender(int dpi)
    {
        if (Renderer == null)
            throw new InvalidOperationException($"{nameof(SvgAnnotation)}.{nameof(Renderer)} not initialized");
        var bitmap = Renderer.RenderForAnnotation(this, dpi);
        ReplaceRenderedBitmap(bitmap, dpi);
    }

    public override Annotation Clone()
    {
        var copy = new SvgAnnotation
        {
            X = X, Y = Y, Rotation = Rotation, PageIndex = PageIndex,
            SvgFilePath = SvgFilePath, Scale = Scale, IsRaster = IsRaster,
            OriginalWidthPt = OriginalWidthPt, OriginalHeightPt = OriginalHeightPt,
            WidthPt = WidthPt, HeightPt = HeightPt,
        };
        if (Renderer != null && !string.IsNullOrEmpty(SvgFilePath) && File.Exists(SvgFilePath))
            copy.ReRender(RenderedDpi > 0 ? RenderedDpi : PdfConstants.RenderDpi);
        return copy;
    }

    public override void Dispose()
    {
        RenderedBitmap?.Dispose();
        RenderedBitmap = null;
    }
}
