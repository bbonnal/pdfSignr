using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using pdfSignr.Models;
using static pdfSignr.Views.InteractionConstants;

namespace pdfSignr.Views;

// --- Routed event args ---

public class CanvasClickedEventArgs(RoutedEvent routedEvent, int pageIndex, double pdfX, double pdfY)
    : RoutedEventArgs(routedEvent)
{
    public int PageIndex { get; } = pageIndex;
    public double PdfX { get; } = pdfX;
    public double PdfY { get; } = pdfY;
}

public class AnnotationSelectedEventArgs(RoutedEvent routedEvent, Annotation? annotation)
    : RoutedEventArgs(routedEvent)
{
    public Annotation? Annotation { get; } = annotation;
}

public class AnnotationManipulatedEventArgs(
    RoutedEvent routedEvent, Annotation annotation,
    double oldX, double oldY, double oldW, double oldH, double oldRot,
    double newX, double newY, double newW, double newH, double newRot)
    : RoutedEventArgs(routedEvent)
{
    public Annotation Annotation { get; } = annotation;
    public double OldX { get; } = oldX;
    public double OldY { get; } = oldY;
    public double OldW { get; } = oldW;
    public double OldH { get; } = oldH;
    public double OldRot { get; } = oldRot;
    public double NewX { get; } = newX;
    public double NewY { get; } = newY;
    public double NewW { get; } = newW;
    public double NewH { get; } = newH;
    public double NewRot { get; } = newRot;
}

/// <summary>
/// A single PDF page surface. Hosts a page bitmap, a list of annotations, and a pointer
/// state machine for annotation manipulation. Rendering and hit-testing are delegated
/// to <see cref="AnnotationRenderer"/> and <see cref="AnnotationHitTester"/>; this class
/// owns only the state machine and Avalonia control plumbing.
/// </summary>
public class PageCanvas : Control
{
    private const double Scale = PdfConstants.DpiScale;

    // Cached cursors (allocation-free reuse)
    private static readonly Cursor CursorHand = new(StandardCursorType.Hand);
    private static readonly Cursor CursorTopLeft = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor CursorTopRight = new(StandardCursorType.TopRightCorner);
    private static readonly Cursor CursorDragMove = new(StandardCursorType.DragMove);

    // Pointer state
    private enum State { Idle, Dragging, Resizing, Rotating }
    private State _state = State.Idle;
    private AnnotationHandle _activeHandle = AnnotationHandle.None;
    private Annotation? _target;
    private Point _dragStart;
    private double _startX, _startY, _startW, _startH, _startRot, _startAngle;

    // ═══ Routed events ═══

    public static readonly RoutedEvent<CanvasClickedEventArgs> CanvasClickedEvent =
        RoutedEvent.Register<PageCanvas, CanvasClickedEventArgs>(nameof(CanvasClicked), RoutingStrategies.Bubble);
    public static readonly RoutedEvent<AnnotationSelectedEventArgs> AnnotationSelectedEvent =
        RoutedEvent.Register<PageCanvas, AnnotationSelectedEventArgs>(nameof(AnnotationSelected), RoutingStrategies.Bubble);
    public static readonly RoutedEvent<RoutedEventArgs> DeleteRequestedEvent =
        RoutedEvent.Register<PageCanvas, RoutedEventArgs>(nameof(DeleteRequested), RoutingStrategies.Bubble);
    public static readonly RoutedEvent<AnnotationManipulatedEventArgs> AnnotationManipulatedEvent =
        RoutedEvent.Register<PageCanvas, AnnotationManipulatedEventArgs>(nameof(AnnotationManipulated), RoutingStrategies.Bubble);

    public event EventHandler<CanvasClickedEventArgs> CanvasClicked
    { add => AddHandler(CanvasClickedEvent, value); remove => RemoveHandler(CanvasClickedEvent, value); }
    public event EventHandler<AnnotationSelectedEventArgs> AnnotationSelected
    { add => AddHandler(AnnotationSelectedEvent, value); remove => RemoveHandler(AnnotationSelectedEvent, value); }
    public event EventHandler<RoutedEventArgs> DeleteRequested
    { add => AddHandler(DeleteRequestedEvent, value); remove => RemoveHandler(DeleteRequestedEvent, value); }
    public event EventHandler<AnnotationManipulatedEventArgs> AnnotationManipulated
    { add => AddHandler(AnnotationManipulatedEvent, value); remove => RemoveHandler(AnnotationManipulatedEvent, value); }

    // ═══ Styled properties ═══

    public static readonly StyledProperty<Bitmap?> PageBitmapProperty =
        AvaloniaProperty.Register<PageCanvas, Bitmap?>(nameof(PageBitmap));
    public static readonly StyledProperty<int> PageIndexProperty =
        AvaloniaProperty.Register<PageCanvas, int>(nameof(PageIndex));
    public static readonly StyledProperty<ObservableCollection<Annotation>?> AnnotationsProperty =
        AvaloniaProperty.Register<PageCanvas, ObservableCollection<Annotation>?>(nameof(Annotations));
    public static readonly StyledProperty<double> PageWidthPtProperty =
        AvaloniaProperty.Register<PageCanvas, double>(nameof(PageWidthPt));
    public static readonly StyledProperty<double> PageHeightPtProperty =
        AvaloniaProperty.Register<PageCanvas, double>(nameof(PageHeightPt));
    public static readonly StyledProperty<Cursor?> PlacementCursorProperty =
        AvaloniaProperty.Register<PageCanvas, Cursor?>(nameof(PlacementCursor));

    public Bitmap? PageBitmap { get => GetValue(PageBitmapProperty); set => SetValue(PageBitmapProperty, value); }
    public int PageIndex { get => GetValue(PageIndexProperty); set => SetValue(PageIndexProperty, value); }
    public ObservableCollection<Annotation>? Annotations { get => GetValue(AnnotationsProperty); set => SetValue(AnnotationsProperty, value); }
    public double PageWidthPt { get => GetValue(PageWidthPtProperty); set => SetValue(PageWidthPtProperty, value); }
    public double PageHeightPt { get => GetValue(PageHeightPtProperty); set => SetValue(PageHeightPtProperty, value); }
    public Cursor? PlacementCursor { get => GetValue(PlacementCursorProperty); set => SetValue(PlacementCursorProperty, value); }

    public PageCanvas()
    {
        ClipToBounds = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    static PageCanvas() => AffectsRender<PageCanvas>(PageBitmapProperty, AnnotationsProperty, PageWidthPtProperty, PageHeightPtProperty);

    // ═══ Property wiring ═══

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AnnotationsProperty)
        {
            if (change.OldValue is ObservableCollection<Annotation> old)
            { old.CollectionChanged -= OnCollChanged; foreach (var a in old) a.PropertyChanged -= OnAnnChanged; }
            if (change.NewValue is ObservableCollection<Annotation> c)
            { c.CollectionChanged += OnCollChanged; foreach (var a in c) a.PropertyChanged += OnAnnChanged; }
            InvalidateVisual(); InvalidateMeasure();
        }
        if (change.Property == PageBitmapProperty) InvalidateVisual();
        if (change.Property == PageWidthPtProperty || change.Property == PageHeightPtProperty)
        { InvalidateVisual(); InvalidateMeasure(); }
    }

    private void OnCollChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (Annotation a in e.OldItems) a.PropertyChanged -= OnAnnChanged;
        if (e.NewItems != null) foreach (Annotation a in e.NewItems) a.PropertyChanged += OnAnnChanged;
        InvalidateVisual();
    }

    private void OnAnnChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Annotation.X) or nameof(Annotation.Y)
            or nameof(Annotation.Rotation) or nameof(Annotation.IsSelected)
            or nameof(TextAnnotation.Text) or nameof(TextAnnotation.FontFamily) or nameof(TextAnnotation.FontSize)
            or nameof(Annotation.WidthPt) or nameof(Annotation.HeightPt)
            or nameof(SvgAnnotation.RenderedBitmap) or nameof(SvgAnnotation.Scale)
            or null)
        {
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (PageWidthPt > 0 && PageHeightPt > 0)
            return new Size(PageWidthPt * Scale, PageHeightPt * Scale);
        if (PageBitmap != null)
            return new Size(PageBitmap.Size.Width, PageBitmap.Size.Height);
        return new Size(100, 100);
    }

    // ═══ Rendering (delegated) ═══

    public override void Render(DrawingContext ctx)
    {
        AnnotationRenderer.DrawPage(ctx, PageBitmap, PageWidthPt, PageHeightPt, Scale);
        AnnotationRenderer.DrawAnnotations(ctx, Annotations, Scale);
    }

    // ═══ Pointer interaction ═══

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        // Right-click bubbles to ContextMenu
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            return;

        var pos = e.GetPosition(this);

        // 1) Handle hits on the selected annotation
        var selected = Annotations?.FirstOrDefault(a => a.IsSelected);
        if (selected != null)
        {
            var h = AnnotationHitTester.HitHandle(selected, pos, Scale);
            if (h == AnnotationHandle.Delete)
            {
                RaiseEvent(new RoutedEventArgs(DeleteRequestedEvent));
                e.Handled = true;
                return;
            }
            if (h != AnnotationHandle.None)
            {
                BeginHandleInteraction(selected, h, pos);
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        // 2) Body hits (topmost first)
        if (Annotations != null)
        {
            for (int i = Annotations.Count - 1; i >= 0; i--)
            {
                if (AnnotationHitTester.HitBody(Annotations[i], pos, Scale))
                {
                    RaiseEvent(new AnnotationSelectedEventArgs(AnnotationSelectedEvent, Annotations[i]));
                    _state = State.Dragging;
                    _target = Annotations[i];
                    _dragStart = pos;
                    _startX = Annotations[i].X;
                    _startY = Annotations[i].Y;
                    _startW = Annotations[i].WidthPt;
                    _startH = Annotations[i].HeightPt;
                    _startRot = Annotations[i].Rotation;
                    Cursor = CursorDragMove;
                    e.Pointer.Capture(this);
                    e.Handled = true;
                    return;
                }
            }
        }

        // 3) Empty space — fire a canvas click for tool placement
        RaiseEvent(new CanvasClickedEventArgs(CanvasClickedEvent, PageIndex, pos.X / Scale, pos.Y / Scale));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);

        switch (_state)
        {
            case State.Dragging when _target != null:
                _target.X = _startX + (pos.X - _dragStart.X) / Scale;
                _target.Y = _startY + (pos.Y - _dragStart.Y) / Scale;
                break;
            case State.Resizing when _target != null:
                DoResize(pos);
                break;
            case State.Rotating when _target != null:
                DoRotate(pos);
                break;
            case State.Idle:
                UpdateIdleCursor(pos);
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_state == State.Dragging && _target != null)
        {
            _target.X = Math.Clamp(_target.X, 0, Math.Max(0, PageWidthPt - _target.WidthPt));
            _target.Y = Math.Clamp(_target.Y, 0, Math.Max(0, PageHeightPt - _target.HeightPt));
        }

        if (_state == State.Resizing && _target is SvgAnnotation svg)
        {
            svg.Scale = svg.OriginalWidthPt > 0 ? svg.WidthPt / svg.OriginalWidthPt : 1;
            svg.ReRender(PdfConstants.RenderDpi);
        }

        if (_target != null && _state != State.Idle)
        {
            bool changed = _target.X != _startX || _target.Y != _startY
                        || _target.WidthPt != _startW || _target.HeightPt != _startH
                        || _target.Rotation != _startRot;
            if (changed)
            {
                RaiseEvent(new AnnotationManipulatedEventArgs(
                    AnnotationManipulatedEvent, _target,
                    _startX, _startY, _startW, _startH, _startRot,
                    _target.X, _target.Y, _target.WidthPt, _target.HeightPt, _target.Rotation));
            }
        }

        _state = State.Idle;
        _activeHandle = AnnotationHandle.None;
        _target = null;
        e.Pointer.Capture(null);
        UpdateIdleCursor(e.GetPosition(this));
    }

    private void BeginHandleInteraction(Annotation ann, AnnotationHandle h, Point pos)
    {
        _target = ann;
        _activeHandle = h;
        _dragStart = pos;
        _startW = ann.WidthPt;
        _startH = ann.HeightPt;
        _startX = ann.X;
        _startY = ann.Y;
        _startRot = ann.Rotation;

        if (h == AnnotationHandle.Rotate)
        {
            _state = State.Rotating;
            var rect = AnnotationHitTester.ScreenRect(ann, Scale);
            var c = rect.Center;
            _startAngle = Math.Atan2(pos.Y - c.Y, pos.X - c.X);
        }
        else
        {
            _state = State.Resizing;
        }
    }

    private void DoResize(Point pos)
    {
        if (_target == null) return;

        double rawDx = pos.X - _dragStart.X;
        double rawDy = pos.Y - _dragStart.Y;

        if (_target.Rotation != 0)
        {
            double a = -_target.Rotation * Math.PI / 180.0;
            double cos = Math.Cos(a), sin = Math.Sin(a);
            (rawDx, rawDy) = (rawDx * cos - rawDy * sin, rawDx * sin + rawDy * cos);
        }

        double dx = rawDx / Scale;
        double dy = rawDy / Scale;

        double sx = _activeHandle is AnnotationHandle.TL or AnnotationHandle.BL ? -1 : 1;
        double sy = _activeHandle is AnnotationHandle.TL or AnnotationHandle.TR ? -1 : 1;

        double newW, newH;

        if (_target is SvgAnnotation && _startW > 0 && _startH > 0)
        {
            double origDiag = Math.Sqrt(_startW * _startW + _startH * _startH);
            double diagDirX = sx * _startW / origDiag;
            double diagDirY = sy * _startH / origDiag;
            double proj = dx * diagDirX + dy * diagDirY;
            double ratio = Math.Max(MinSizePt / Math.Min(_startW, _startH),
                                    (origDiag + proj) / origDiag);
            newW = _startW * ratio;
            newH = _startH * ratio;
        }
        else
        {
            newW = Math.Max(MinSizePt, _startW + sx * dx);
            newH = Math.Max(MinSizePt, _startH + sy * dy);
        }

        _target.WidthPt = newW;
        _target.HeightPt = newH;

        _target.X = sx < 0 ? _startX + _startW - newW : _startX;
        _target.Y = sy < 0 ? _startY + _startH - newH : _startY;
    }

    private void DoRotate(Point pos)
    {
        if (_target == null) return;
        var rect = AnnotationHitTester.ScreenRect(_target, Scale);
        var c = rect.Center;
        double current = Math.Atan2(pos.Y - c.Y, pos.X - c.X);
        double delta = (current - _startAngle) * 180.0 / Math.PI;
        _target.Rotation = _startRot + delta;
    }

    private void UpdateIdleCursor(Point pos)
    {
        var fallback = PlacementCursor;

        var sel = Annotations?.FirstOrDefault(a => a.IsSelected);
        if (sel != null)
        {
            var h = AnnotationHitTester.HitHandle(sel, pos, Scale);
            if (h != AnnotationHandle.None)
            {
                Cursor = h switch
                {
                    AnnotationHandle.TL or AnnotationHandle.BR => CursorTopLeft,
                    AnnotationHandle.TR or AnnotationHandle.BL => CursorTopRight,
                    _ => CursorHand
                };
                return;
            }
        }

        if (Annotations != null)
        {
            for (int i = Annotations.Count - 1; i >= 0; i--)
            {
                if (AnnotationHitTester.HitBody(Annotations[i], pos, Scale))
                {
                    Cursor = CursorHand;
                    return;
                }
            }
        }

        Cursor = fallback;
    }
}
