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

    public void RotateAnnotations(int degrees, double oldW, double oldH)
    {
        foreach (var ann in Annotations)
        {
            var (x, y, w, h) = (ann.X, ann.Y, ann.WidthPt, ann.HeightPt);
            switch (degrees)
            {
                case 90:
                    ann.X = oldH - y - h;
                    ann.Y = x;
                    break;
                case 180:
                    ann.X = oldW - x - w;
                    ann.Y = oldH - y - h;
                    break;
                case 270:
                    ann.X = y;
                    ann.Y = oldW - x - w;
                    break;
            }
        }
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
