using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace pdfSignr.ViewModels;

/// <summary>
/// Viewport-scoped state (zoom, current page, layout mode, border thicknesses).
/// The View writes these properties; XAML binds to them. No compute logic lives here —
/// zoom math and scroll tracking remain in the view code-behind because they need
/// direct access to ScrollViewer/LayoutTransform.
/// </summary>
public partial class ViewportViewModel : ObservableObject
{
    [ObservableProperty] private int _zoomPercent = 100;
    [ObservableProperty] private int _currentPageInView = 1;
    [ObservableProperty] private double _buttonScale = 1.0;
    [ObservableProperty] private Thickness _selectionBorderThickness = new(3);
    [ObservableProperty] private Thickness _hitBorderThickness = new(28);
    [ObservableProperty] private Thickness _hitBorderMargin = new(-28);
    [ObservableProperty] private bool _isGridMode;

    public bool IsNotGridMode => !IsGridMode;

    partial void OnIsGridModeChanged(bool value) => OnPropertyChanged(nameof(IsNotGridMode));
}
