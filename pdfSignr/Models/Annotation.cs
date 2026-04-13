using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

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

    // Font size is derived from bounding box height
    public double FontSize => ComputeFontSize();

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(FontSize));
    partial void OnFontFamilyChanged(string value) => OnPropertyChanged(nameof(FontSize));

    public override double WidthPt
    {
        get => _widthPt;
        set => SetProperty(ref _widthPt, value);
    }

    public override double HeightPt
    {
        get => _heightPt;
        set { if (SetProperty(ref _heightPt, value)) OnPropertyChanged(nameof(FontSize)); }
    }

    private static readonly Dictionary<string, Typeface> TypefaceCache = new();

    private double ComputeFontSize()
    {
        if (_heightPt <= 0) return 12;
        // Measure text at a reference size to find the ratio
        const double refSize = 72.0;
        const double dpiScale = 150.0 / 72.0;
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

    internal static FontFamily MapFontForMeasure(string pdfFont) => pdfFont switch
    {
        "Helvetica" => new FontFamily("avares://pdfSignr/Assets/Fonts/LiberationSans-Regular.ttf#Liberation Sans"),
        "Times-Roman" => new FontFamily("avares://pdfSignr/Assets/Fonts/LiberationSerif-Regular.ttf#Liberation Serif"),
        "Courier" => new FontFamily("avares://pdfSignr/Assets/Fonts/LiberationMono-Regular.ttf#Liberation Mono"),
        _ => new FontFamily("avares://pdfSignr/Assets/Fonts/LiberationSans-Regular.ttf#Liberation Sans"),
    };
}

public partial class SvgAnnotation : Annotation
{
    [ObservableProperty] private string _svgFilePath = "";
    [ObservableProperty] private double _scale = 1.0;
    [ObservableProperty] private double _originalWidthPt;
    [ObservableProperty] private double _originalHeightPt;
    private double _svgWidthPt;
    private double _svgHeightPt;

    public Bitmap? RenderedBitmap { get; set; }

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
}
