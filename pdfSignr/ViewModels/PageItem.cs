using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using pdfSignr.Models;

namespace pdfSignr.ViewModels;

public partial class PageItem : ObservableObject
{
    [ObservableProperty] private int _index;
    [ObservableProperty] private Bitmap? _bitmap;
    [ObservableProperty] private bool _isFirst;
    [ObservableProperty] private bool _isLast;
    [ObservableProperty] private int _displayNumber = 1;
    [ObservableProperty] private bool _isDropTargetAfter;
    [ObservableProperty] private bool _isDropTargetBefore;
    public double WidthPt { get; init; }
    public double HeightPt { get; init; }
    public PageSource Source { get; init; } = null!;
    public ObservableCollection<Annotation> Annotations { get; } = new();

    public void DisposeResources()
    {
        Bitmap?.Dispose();
        Bitmap = null;
        foreach (var ann in Annotations)
            if (ann is SvgAnnotation svg)
                svg.Dispose();
    }
}
