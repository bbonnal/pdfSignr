using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using pdfSignr.Models;
using pdfSignr.Services;
using pdfSignr.ViewModels;

namespace pdfSignr.Views;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext!;
    private TextAnnotation? _editingText;
    private double _zoom = 1.0;
    private int _renderedDpi = MainViewModel.RenderDpi;
    private DispatcherTimer? _rerenderTimer;
    private CancellationTokenSource? _rerenderCts;

    public MainWindow()
    {
        InitializeComponent();

        AddHandler(PageCanvas.CanvasClickedEvent, OnCanvasClicked);
        AddHandler(PageCanvas.AnnotationSelectedEvent, OnAnnotationSelected);
        AddHandler(PageCanvas.DeleteRequestedEvent, OnDeleteRequested);
        // Tunnel so we get the event before ScrollViewer consumes it
        PdfScrollViewer.AddHandler(PointerWheelChangedEvent, OnScrollWheel, RoutingStrategies.Tunnel);

        // Drag-and-drop for PDF files
        PdfScrollViewer.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        PdfScrollViewer.AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        PdfScrollViewer.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        PdfScrollViewer.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Size window to full vertical extent of the current screen
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen != null)
        {
            var scaling = screen.Scaling;
            var workArea = screen.WorkingArea;

            // Account for window frame decorations (title bar + borders)
            double frameOverhead = FrameSize is { } frame
                ? frame.Height - ClientSize.Height
                : 32; // safe fallback for typical title bar

            double dipHeight = workArea.Height / scaling - frameOverhead;
            double dipWidth = Math.Max(600, workArea.Width / scaling * 0.6);
            Height = dipHeight;
            Width = dipWidth;
            Position = new PixelPoint(
                workArea.X + (int)((workArea.Width - dipWidth * scaling) / 2),
                workArea.Y);
        }
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.PdfLoaded += OnPdfLoaded;
    }

    // ═══ ViewModel tracking ═══

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedAnnotation))
        {
            UpdateTextEditor();
        }
    }

    // ═══ Zoom ═══

    private void OnScrollWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        double step = e.Delta.Y > 0 ? 0.1 : -0.1;
        var cursorInViewport = e.GetPosition(PdfScrollViewer);
        ApplyZoom(Math.Clamp(_zoom + step, 0.1, 5.0), cursorInViewport);
        e.Handled = true;
    }

    private void ApplyZoom(double level, Point? anchor = null)
    {
        double oldZoom = _zoom;
        _zoom = level;

        // Compute content point under anchor before zoom
        double anchorContentX = 0, anchorContentY = 0;
        double anchorX = 0, anchorY = 0;
        if (anchor is { } pt)
        {
            anchorX = pt.X;
            anchorY = pt.Y;
            anchorContentX = (PdfScrollViewer.Offset.X + anchorX) / oldZoom;
            anchorContentY = (PdfScrollViewer.Offset.Y + anchorY) / oldZoom;
        }

        ZoomTransform.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        ViewModel.ZoomPercent = (int)Math.Round(_zoom * 100);
        ViewModel.UpdateStatusText();

        // Adjust scroll so content point stays under anchor
        if (anchor is not null)
        {
            // Force layout so ScrollViewer extent updates
            ZoomTransform.UpdateLayout();
            double newOffX = anchorContentX * _zoom - anchorX;
            double newOffY = anchorContentY * _zoom - anchorY;
            PdfScrollViewer.Offset = new Vector(
                Math.Max(0, newOffX),
                Math.Max(0, newOffY));
        }

        ScheduleRerender();
    }

    public void FitToWidth()
    {
        if (ViewModel.Pages.Count == 0) return;

        double available = PdfScrollViewer.Bounds.Width - 40;
        if (available <= 0) return;

        double pageScreenWidth = ViewModel.Pages[0].WidthPt * (MainViewModel.RenderDpi / 72.0);
        if (pageScreenWidth <= 0) return;

        // Preserve relative vertical position through the zoom change
        double oldExtentH = PdfScrollViewer.Extent.Height;
        double relativeY = oldExtentH > 0
            ? (PdfScrollViewer.Offset.Y + PdfScrollViewer.Viewport.Height / 2) / oldExtentH
            : 0;

        var viewportCenter = new Point(
            PdfScrollViewer.Viewport.Width / 2,
            PdfScrollViewer.Viewport.Height / 2);
        ApplyZoom(Math.Clamp(available / pageScreenWidth, 0.1, 5.0), viewportCenter);
    }

    // ═══ Adaptive DPI re-render ═══

    private void ScheduleRerender()
    {
        int targetDpi = QuantizeDpi((int)(MainViewModel.RenderDpi * _zoom));
        if (targetDpi == _renderedDpi) return;

        // Cancel any in-flight render and restart the debounce timer
        _rerenderCts?.Cancel();

        if (_rerenderTimer == null)
        {
            _rerenderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _rerenderTimer.Tick += (_, _) =>
            {
                _rerenderTimer.Stop();
                _ = RerenderPagesAsync();
            };
        }
        else
        {
            _rerenderTimer.Stop();
        }
        _rerenderTimer.Start();
    }

    private static int QuantizeDpi(int dpi)
    {
        if (dpi <= 100) return 96;
        if (dpi <= 150) return 150;
        if (dpi <= 225) return 200;
        if (dpi <= 350) return 300;
        return 400;
    }

    private async Task RerenderPagesAsync()
    {
        int targetDpi = QuantizeDpi((int)(MainViewModel.RenderDpi * _zoom));
        if (targetDpi == _renderedDpi) return;
        _renderedDpi = targetDpi;

        _rerenderCts?.Cancel();
        var cts = new CancellationTokenSource();
        _rerenderCts = cts;

        // Snapshot page sources so we can render off-thread
        var snapshot = ViewModel.Pages
            .Select(p => (Page: p, p.Source.PdfBytes, p.Source.SourcePageIndex))
            .ToList();

        foreach (var (page, pdfBytes, srcIdx) in snapshot)
        {
            if (cts.Token.IsCancellationRequested) return;

            // Render single page on background thread
            var bitmap = await Task.Run(
                () => PdfRenderService.RenderPage(pdfBytes, srcIdx, targetDpi),
                cts.Token);

            if (cts.Token.IsCancellationRequested)
            {
                bitmap.Dispose();
                return;
            }

            var old = page.Bitmap;
            page.Bitmap = bitmap;
            old?.Dispose();
        }
    }


    // ═══ Inline text editor ═══

    private void UpdateTextEditor()
    {
        if (ViewModel.SelectedAnnotation is TextAnnotation text)
            ShowTextEditor(text);
        else
            HideTextEditor();
    }

    private void ShowTextEditor(TextAnnotation text)
    {
        // Detach old
        if (_editingText != null)
        {
            _editingText.PropertyChanged -= OnEditingAnnotationMoved;
            InlineTextBox.TextChanged -= OnInlineTextChanged;
            InlineFontCombo.SelectionChanged -= OnInlineFontChanged;
        }

        _editingText = text;

        // Set values
        InlineTextBox.Text = text.Text;
        InlineFontCombo.SelectedItem = text.FontFamily;

        // Attach live bindings
        InlineTextBox.TextChanged += OnInlineTextChanged;
        InlineFontCombo.SelectionChanged += OnInlineFontChanged;
        _editingText.PropertyChanged += OnEditingAnnotationMoved;

        // Position and show
        PositionEditorOverlay();
        TextEditorOverlay.IsVisible = true;
        InlineTextBox.Focus();
        InlineTextBox.SelectAll();
    }

    private void HideTextEditor()
    {
        if (_editingText != null)
        {
            _editingText.PropertyChanged -= OnEditingAnnotationMoved;
            InlineTextBox.TextChanged -= OnInlineTextChanged;
            InlineFontCombo.SelectionChanged -= OnInlineFontChanged;
            _editingText = null;
        }
        TextEditorOverlay.IsVisible = false;
    }

    private void OnInlineTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_editingText != null && InlineTextBox.Text != null)
            _editingText.Text = InlineTextBox.Text;
    }

    private void OnInlineFontChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_editingText != null && InlineFontCombo.SelectedItem is string font)
            _editingText.FontFamily = font;
    }

    private void OnEditingAnnotationMoved(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "X" or "Y" or "HeightPt")
            PositionEditorOverlay();
    }

    private void PositionEditorOverlay()
    {
        if (_editingText == null) return;

        const double dpiScale = 150.0 / 72.0;
        double annScreenX = _editingText.X * dpiScale;
        double annScreenBottom = (_editingText.Y + _editingText.HeightPt) * dpiScale + 8;

        // Find the PageCanvas hosting this annotation
        var canvas = FindCanvasForAnnotation(_editingText);
        if (canvas == null) return;

        var transform = canvas.TransformToVisual(this);
        if (transform == null) return;

        var pos = transform.Value.Transform(new Point(annScreenX, annScreenBottom));

        // Clamp within window bounds
        pos = new Point(
            Math.Clamp(pos.X, 50, Math.Max(50, Bounds.Width - 230)),
            Math.Clamp(pos.Y, 0, Math.Max(0, Bounds.Height - 100)));

        TextEditorOverlay.Margin = new Thickness(pos.X, pos.Y, 0, 0);
    }

    private PageCanvas? FindCanvasForAnnotation(Annotation ann)
    {
        var items = PdfScrollViewer.Content as LayoutTransformControl;
        var itemsControl = items?.Child as ItemsControl;
        if (itemsControl == null) return null;

        foreach (var container in itemsControl.GetRealizedContainers())
        {
            var canvas = FindDescendant<PageCanvas>(container);
            if (canvas?.Annotations?.Contains(ann) == true)
                return canvas;
        }
        return null;
    }

    private static T? FindDescendant<T>(Control root) where T : Control
    {
        if (root is T match) return match;
        if (root is ContentPresenter cp && cp.Child is Control cpChild)
            return FindDescendant<T>(cpChild);
        if (root is Decorator d && d.Child is Control dChild)
            return FindDescendant<T>(dChild);
        if (root is Panel p)
        {
            foreach (var child in p.Children)
            {
                if (child is Control cc)
                {
                    var found = FindDescendant<T>(cc);
                    if (found != null) return found;
                }
            }
        }
        return null;
    }

    // ═══ Drag-and-drop ═══

    private static bool HasPdfFiles(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        return files?.Any(f => f.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) == true;
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (HasPdfFiles(e))
            ViewModel.IsDraggingFile = true;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;
        if (HasPdfFiles(e))
        {
            e.DragEffects = DragDropEffects.Copy;
            ViewModel.IsDraggingFile = true;
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        ViewModel.IsDraggingFile = false;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        ViewModel.IsDraggingFile = false;

        var files = e.DataTransfer.TryGetFiles();
        var pdfFile = files?.FirstOrDefault(f => f.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        var path = pdfFile?.TryGetLocalPath();
        if (path == null) return;

        if (ViewModel.Pages.Count == 0)
        {
            ViewModel.LoadPdf(path);
        }
        else
        {
            int insertIndex = GetInsertionIndex(e);
            ViewModel.InsertPagesFromFile(path, insertIndex);
        }
    }

    private int GetInsertionIndex(DragEventArgs e)
    {
        if (ViewModel.Pages.Count == 0) return 0;

        var dropPos = e.GetPosition(PdfScrollViewer);
        var itemsControl = (ItemsControl)ZoomTransform.Child!;

        for (int i = 0; i < ViewModel.Pages.Count; i++)
        {
            var container = itemsControl.ContainerFromIndex(i);
            if (container == null) continue;

            // Translate container midpoint to ScrollViewer coordinates
            var mid = new Point(0, container.Bounds.Height / 2);
            var inScrollViewer = container.TranslatePoint(mid, PdfScrollViewer);
            if (inScrollViewer == null) continue;

            if (dropPos.Y < inScrollViewer.Value.Y)
                return i;
        }

        return ViewModel.Pages.Count;
    }

    // ═══ Fit-to-width on load ═══

    private void OnPdfLoaded()
    {
        // Delay until layout has completed so FitToWidth can measure page widths
        Dispatcher.UIThread.Post(FitToWidth, DispatcherPriority.Background);
    }

    // ═══ Keyboard ═══

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Don't intercept when editing text
        if (TextEditorOverlay.IsVisible && InlineTextBox.IsFocused)
        {
            if (e.Key == Key.Escape)
            {
                HideTextEditor();
                e.Handled = true;
            }
            return;
        }

        base.OnKeyDown(e);

        if (e.Key == Key.Delete || e.Key == Key.Back)
        {
            if (ViewModel.DeleteCommand.CanExecute(null))
            {
                HideTextEditor();
                ViewModel.DeleteCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            HideTextEditor();
            ViewModel.CurrentTool = "Select";
            ViewModel.SelectAnnotation(null);
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.D0 || e.Key == Key.NumPad0)
            { FitToWidth(); e.Handled = true; }
            else if (e.Key == Key.OemPlus || e.Key == Key.Add)
            { ApplyZoom(Math.Clamp(_zoom + 0.1, 0.1, 5.0)); e.Handled = true; }
            else if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
            { ApplyZoom(Math.Clamp(_zoom - 0.1, 0.1, 5.0)); e.Handled = true; }
        }
    }

    // ═══ Canvas events ═══

    private void OnCanvasClicked(object? sender, CanvasClickedEventArgs e)
    {
        HideTextEditor();
        ViewModel.OnCanvasClicked(e.PageIndex, e.PdfX, e.PdfY);
    }

    private void OnAnnotationSelected(object? sender, AnnotationSelectedEventArgs e)
    {
        ViewModel.SelectAnnotation(e.Annotation);
    }

    private void OnDeleteRequested(object? sender, RoutedEventArgs e)
    {
        HideTextEditor();
        if (ViewModel.DeleteCommand.CanExecute(null))
            ViewModel.DeleteCommand.Execute(null);
    }

    // ═══ Zoom buttons ═══

    private void OnFitToWidth(object? sender, RoutedEventArgs e) => FitToWidth();
    private void OnZoomIn(object? sender, RoutedEventArgs e) => ApplyZoom(Math.Clamp(_zoom + 0.1, 0.1, 5.0));
    private void OnZoomOut(object? sender, RoutedEventArgs e) => ApplyZoom(Math.Clamp(_zoom - 0.1, 0.1, 5.0));

    // ═══ Theme toggle ═══

    private bool _isDark = true; // dark by default

    private void OnThemeToggle(object? sender, RoutedEventArgs e)
    {
        _isDark = !_isDark;
        Application.Current!.RequestedThemeVariant = _isDark ? ThemeVariant.Dark : ThemeVariant.Light;

        var key = _isDark ? "IconSun" : "IconMoon";
        if (Application.Current.TryFindResource(key, out var res) && res is StreamGeometry geo)
            ThemeIcon.Data = geo;
    }
}
