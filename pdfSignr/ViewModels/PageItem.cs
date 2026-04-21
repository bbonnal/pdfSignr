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
    [ObservableProperty] private int _displayNumber = 1;
    [ObservableProperty] private bool _isDropTargetAfter;
    [ObservableProperty] private bool _isDropTargetBefore;
    [ObservableProperty] private int _rotationDegrees;
    [ObservableProperty] private bool _isSelected;

    public double OriginalWidthPt { get; init; }
    public double OriginalHeightPt { get; init; }

    public double WidthPt => RotationDegrees is 90 or 270 ? OriginalHeightPt : OriginalWidthPt;
    public double HeightPt => RotationDegrees is 90 or 270 ? OriginalWidthPt : OriginalHeightPt;

    public PageSource Source { get; init; } = null!;
    public MainViewModel ParentVM { get; init; } = null!;
    public ObservableCollection<Annotation> Annotations { get; } = new();

    partial void OnRotationDegreesChanged(int value)
    {
        OnPropertyChanged(nameof(WidthPt));
        OnPropertyChanged(nameof(HeightPt));
    }

    /// <summary>Computes an annotation's post-rotation (X, Y) without mutating state.</summary>
    public static (double X, double Y) RotatedPosition(
        int degrees, double x, double y, double w, double h, double oldW, double oldH) => degrees switch
    {
        90 => (oldH - y - h, x),
        180 => (oldW - x - w, oldH - y - h),
        270 => (y, oldW - x - w),
        _ => (x, y)
    };

    /// <summary>
    /// Deep copy suitable for a paste operation: same source PDF bytes and rotation,
    /// cloned annotations, no bitmap (the view will render one on demand).
    /// </summary>
    public PageItem Clone()
    {
        var copy = new PageItem
        {
            Source = Source,
            ParentVM = ParentVM,
            OriginalWidthPt = OriginalWidthPt,
            OriginalHeightPt = OriginalHeightPt,
            RotationDegrees = RotationDegrees,
        };
        foreach (var ann in Annotations)
            copy.Annotations.Add(ann.Clone());
        return copy;
    }

    /// <summary>Swaps the page bitmap and disposes the old one. Must be called on the UI thread.</summary>
    public void ReplaceBitmap(Bitmap? newBitmap)
    {
        var old = Bitmap;
        Bitmap = newBitmap;
        old?.Dispose();
    }

    public void DisposeResources()
    {
        Bitmap?.Dispose();
        Bitmap = null;
        foreach (var ann in Annotations)
            ann.Dispose();
        Annotations.Clear();
    }
}
