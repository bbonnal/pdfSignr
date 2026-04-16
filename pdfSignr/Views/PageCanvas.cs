using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using pdfSignr.Models;

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

// --- PageCanvas ---

public class PageCanvas : Control
{
    private const double Scale = PdfConstants.DpiScale;
    private const double HandleRadius = 5;
    private const double HandleHit = 10;
    private const double RotateDistance = 28;
    private const double RotateRadius = 6;
    private const double MinSizePt = 8;
    private const double DeleteSize = 7;
    private const double DeleteOffset = 14;
    private const double HitBodyInflate = 4;

    // Cached cursors to avoid allocations on every mouse move
    private static readonly Cursor CursorHand = new(StandardCursorType.Hand);
    private static readonly Cursor CursorTopLeft = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor CursorTopRight = new(StandardCursorType.TopRightCorner);
    private static readonly Cursor CursorDragMove = new(StandardCursorType.DragMove);

    // Cached pens to avoid allocations on every render frame
    private static readonly Pen SelectionDashPen = new(Brushes.DodgerBlue, 1.5, DashStyle.Dash);
    private static readonly Pen HandlePen = new(Brushes.DodgerBlue, 1.5);
    private static readonly Pen DeleteCirclePen = new(Brushes.IndianRed, 1.5);
    private static readonly Pen DeleteXPen = new(Brushes.IndianRed, 2);

    // Interaction state
    private enum State { Idle, Dragging, Resizing, Rotating }
    private enum Handle { None, TL, TR, BL, BR, Rotate, Delete }

    private State _state = State.Idle;
    private Handle _activeHandle = Handle.None;
    private Annotation? _target;
    private Point _dragStart;
    private double _startX, _startY, _startW, _startH, _startRot, _startAngle;

    // Routed events
    public static readonly RoutedEvent<CanvasClickedEventArgs> CanvasClickedEvent =
        RoutedEvent.Register<PageCanvas, CanvasClickedEventArgs>(nameof(CanvasClicked), RoutingStrategies.Bubble);
    public static readonly RoutedEvent<AnnotationSelectedEventArgs> AnnotationSelectedEvent =
        RoutedEvent.Register<PageCanvas, AnnotationSelectedEventArgs>(nameof(AnnotationSelected), RoutingStrategies.Bubble);
    public static readonly RoutedEvent<RoutedEventArgs> DeleteRequestedEvent =
        RoutedEvent.Register<PageCanvas, RoutedEventArgs>(nameof(DeleteRequested), RoutingStrategies.Bubble);

    public event EventHandler<CanvasClickedEventArgs> CanvasClicked
    { add => AddHandler(CanvasClickedEvent, value); remove => RemoveHandler(CanvasClickedEvent, value); }
    public event EventHandler<AnnotationSelectedEventArgs> AnnotationSelected
    { add => AddHandler(AnnotationSelectedEvent, value); remove => RemoveHandler(AnnotationSelectedEvent, value); }
    public event EventHandler<RoutedEventArgs> DeleteRequested
    { add => AddHandler(DeleteRequestedEvent, value); remove => RemoveHandler(DeleteRequestedEvent, value); }

    // Styled properties
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

    // --- Property change wiring ---

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
        if (change.Property == PageBitmapProperty) { InvalidateVisual(); }
        if (change.Property == PageWidthPtProperty || change.Property == PageHeightPtProperty) { InvalidateVisual(); InvalidateMeasure(); }
    }

    private void OnCollChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (Annotation a in e.OldItems) a.PropertyChanged -= OnAnnChanged;
        if (e.NewItems != null) foreach (Annotation a in e.NewItems) a.PropertyChanged += OnAnnChanged;
        InvalidateVisual();
    }
    private void OnAnnChanged(object? s, PropertyChangedEventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize)
    {
        if (PageWidthPt > 0 && PageHeightPt > 0)
            return new Size(PageWidthPt * Scale, PageHeightPt * Scale);
        if (PageBitmap != null)
            return new Size(PageBitmap.Size.Width, PageBitmap.Size.Height);
        return new Size(100, 100);
    }

    // ════════════════════════════════════════
    //  RENDERING
    // ════════════════════════════════════════

    public override void Render(DrawingContext ctx)
    {
        if (PageBitmap != null)
        {
            // Draw bitmap stretched to the fixed layout size (base DPI),
            // so higher-DPI bitmaps provide more detail without changing layout
            var destRect = new Rect(0, 0, PageWidthPt * Scale, PageHeightPt * Scale);
            if (destRect.Width <= 0 || destRect.Height <= 0)
                destRect = new Rect(0, 0, PageBitmap.Size.Width, PageBitmap.Size.Height);
            ctx.DrawImage(PageBitmap, destRect);
        }

        if (Annotations == null) return;

        foreach (var ann in Annotations)
        {
            var rect = ScreenRect(ann);
            if (ann.Rotation != 0)
            {
                var c = rect.Center;
                using (ctx.PushTransform(
                    Matrix.CreateTranslation(-c.X, -c.Y) *
                    Matrix.CreateRotation(ann.Rotation * Math.PI / 180.0) *
                    Matrix.CreateTranslation(c.X, c.Y)))
                {
                    DrawContent(ctx, ann, rect);
                    if (ann.IsSelected) DrawChrome(ctx, rect);
                }
            }
            else
            {
                DrawContent(ctx, ann, rect);
                if (ann.IsSelected) DrawChrome(ctx, rect);
            }
        }
    }

    private void DrawContent(DrawingContext ctx, Annotation ann, Rect rect)
    {
        if (ann is TextAnnotation t)
        {
            var ft = MakeFormattedText(t);
            ctx.DrawText(ft, rect.TopLeft);
        }
        else if (ann is SvgAnnotation svg && svg.RenderedBitmap != null)
        {
            ctx.DrawImage(svg.RenderedBitmap, rect);
        }
    }

    private static void DrawChrome(DrawingContext ctx, Rect rect)
    {
        ctx.DrawRectangle(null, SelectionDashPen, rect.Inflate(2));

        // Corner handles
        DrawCircle(ctx, rect.TopLeft, HandleRadius, Brushes.White, HandlePen);
        DrawCircle(ctx, rect.TopRight, HandleRadius, Brushes.White, HandlePen);
        DrawCircle(ctx, rect.BottomLeft, HandleRadius, Brushes.White, HandlePen);
        DrawCircle(ctx, rect.BottomRight, HandleRadius, Brushes.White, HandlePen);

        // Rotation handle
        var topMid = new Point(rect.Center.X, rect.Top);
        var rotPos = new Point(rect.Center.X, rect.Top - RotateDistance);
        ctx.DrawLine(HandlePen, topMid, rotPos);
        DrawCircle(ctx, rotPos, RotateRadius, Brushes.LightGreen, HandlePen);

        // Delete handle (red circle with X, top-right outside corner)
        var delPos = new Point(rect.Right + DeleteOffset, rect.Top - DeleteOffset);
        DrawCircle(ctx, delPos, HandleRadius + 2, Brushes.White, DeleteCirclePen);
        ctx.DrawLine(DeleteXPen,
            new Point(delPos.X - DeleteSize / 2, delPos.Y - DeleteSize / 2),
            new Point(delPos.X + DeleteSize / 2, delPos.Y + DeleteSize / 2));
        ctx.DrawLine(DeleteXPen,
            new Point(delPos.X + DeleteSize / 2, delPos.Y - DeleteSize / 2),
            new Point(delPos.X - DeleteSize / 2, delPos.Y + DeleteSize / 2));
    }

    private static void DrawCircle(DrawingContext ctx, Point c, double r, IBrush fill, IPen pen) =>
        ctx.DrawEllipse(fill, pen, c, r, r);

    // ════════════════════════════════════════
    //  POINTER INTERACTION
    // ════════════════════════════════════════

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pos = e.GetPosition(this);

        // 1) Check handles on selected annotation
        var selected = Annotations?.FirstOrDefault(a => a.IsSelected);
        if (selected != null)
        {
            var h = HitHandle(selected, pos);
            if (h == Handle.Delete)
            {
                RaiseEvent(new RoutedEventArgs(DeleteRequestedEvent));
                e.Handled = true;
                return;
            }
            if (h != Handle.None)
            {
                BeginHandleInteraction(selected, h, pos);
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        // 2) Hit test annotation bodies
        if (Annotations != null)
        {
            for (int i = Annotations.Count - 1; i >= 0; i--)
            {
                if (HitBody(Annotations[i], pos))
                {
                    RaiseEvent(new AnnotationSelectedEventArgs(AnnotationSelectedEvent, Annotations[i]));
                    _state = State.Dragging;
                    _target = Annotations[i];
                    _dragStart = pos;
                    _startX = Annotations[i].X;
                    _startY = Annotations[i].Y;
                    Cursor = CursorDragMove;
                    e.Pointer.Capture(this);
                    e.Handled = true;
                    return;
                }
            }
        }

        // 3) Empty space
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
            // Clamp annotation to page bounds so it can't be lost off-screen
            _target.X = Math.Clamp(_target.X, 0, Math.Max(0, PageWidthPt - _target.WidthPt));
            _target.Y = Math.Clamp(_target.Y, 0, Math.Max(0, PageHeightPt - _target.HeightPt));
        }

        if (_state == State.Resizing && _target is SvgAnnotation svg)
        {
            svg.Scale = svg.OriginalWidthPt > 0 ? svg.WidthPt / svg.OriginalWidthPt : 1;
            svg.ReRender(PdfConstants.RenderDpi);
        }

        _state = State.Idle;
        _activeHandle = Handle.None;
        _target = null;
        e.Pointer.Capture(null);
        UpdateIdleCursor(e.GetPosition(this));
    }

    // ════════════════════════════════════════
    //  RESIZE / ROTATE LOGIC
    // ════════════════════════════════════════

    private void BeginHandleInteraction(Annotation ann, Handle h, Point pos)
    {
        _target = ann;
        _activeHandle = h;
        _dragStart = pos;
        _startW = ann.WidthPt;
        _startH = ann.HeightPt;
        _startX = ann.X;
        _startY = ann.Y;
        _startRot = ann.Rotation;

        if (h == Handle.Rotate)
        {
            _state = State.Rotating;
            var rect = ScreenRect(ann);
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

        // Delta in screen pixels from drag start
        double rawDx = pos.X - _dragStart.X;
        double rawDy = pos.Y - _dragStart.Y;

        // Inverse-rotate delta into local annotation space
        if (_target.Rotation != 0)
        {
            double a = -_target.Rotation * Math.PI / 180.0;
            double cos = Math.Cos(a), sin = Math.Sin(a);
            (rawDx, rawDy) = (rawDx * cos - rawDy * sin, rawDx * sin + rawDy * cos);
        }

        // Convert to PDF points
        double dx = rawDx / Scale;
        double dy = rawDy / Scale;

        // Sign: handle direction (+1 = dragging increases size, -1 = dragging decreases)
        double sx = _activeHandle is Handle.TL or Handle.BL ? -1 : 1;
        double sy = _activeHandle is Handle.TL or Handle.TR ? -1 : 1;

        double newW, newH;

        if (_target is SvgAnnotation && _startW > 0 && _startH > 0)
        {
            // Aspect-ratio locked: project delta onto the annotation diagonal
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
            // Free resize (text)
            newW = Math.Max(MinSizePt, _startW + sx * dx);
            newH = Math.Max(MinSizePt, _startH + sy * dy);
        }

        // Update size
        _target.WidthPt = newW;
        _target.HeightPt = newH;

        // Shift origin so the opposite corner stays anchored
        _target.X = sx < 0 ? _startX + _startW - newW : _startX;
        _target.Y = sy < 0 ? _startY + _startH - newH : _startY;
    }

    private void DoRotate(Point pos)
    {
        if (_target == null) return;
        var rect = ScreenRect(_target);
        var c = rect.Center;
        double current = Math.Atan2(pos.Y - c.Y, pos.X - c.X);
        double delta = (current - _startAngle) * 180.0 / Math.PI;
        _target.Rotation = _startRot + delta;
    }

    // ════════════════════════════════════════
    //  HIT TESTING
    // ════════════════════════════════════════

    private Handle HitHandle(Annotation ann, Point pos)
    {
        var rect = ScreenRect(ann);
        var c = rect.Center;
        double angle = ann.Rotation * Math.PI / 180.0;

        Point R(Point p) => RotatePoint(p, c, angle);

        // Delete handle (top-right outside)
        var delPos = R(new Point(rect.Right + DeleteOffset, rect.Top - DeleteOffset));
        if (Dist(pos, delPos) <= HandleHit) return Handle.Delete;

        var rotPos = R(new Point(rect.Center.X, rect.Top - RotateDistance));
        if (Dist(pos, rotPos) <= HandleHit) return Handle.Rotate;
        if (Dist(pos, R(rect.TopLeft)) <= HandleHit) return Handle.TL;
        if (Dist(pos, R(rect.TopRight)) <= HandleHit) return Handle.TR;
        if (Dist(pos, R(rect.BottomLeft)) <= HandleHit) return Handle.BL;
        if (Dist(pos, R(rect.BottomRight)) <= HandleHit) return Handle.BR;
        return Handle.None;
    }

    private bool HitBody(Annotation ann, Point pos)
    {
        var rect = ScreenRect(ann);
        if (ann.Rotation != 0)
        {
            var local = RotatePoint(pos, rect.Center, -ann.Rotation * Math.PI / 180.0);
            return rect.Inflate(HitBodyInflate).Contains(local);
        }
        return rect.Inflate(HitBodyInflate).Contains(pos);
    }

    private void UpdateIdleCursor(Point pos)
    {
        var fallback = PlacementCursor;

        // Check selected annotation handles first
        var sel = Annotations?.FirstOrDefault(a => a.IsSelected);
        if (sel != null)
        {
            var h = HitHandle(sel, pos);
            if (h != Handle.None)
            {
                Cursor = h switch
                {
                    Handle.TL or Handle.BR => CursorTopLeft,
                    Handle.TR or Handle.BL => CursorTopRight,
                    _ => CursorHand
                };
                return;
            }
        }

        // Check if hovering any annotation body
        if (Annotations != null)
        {
            for (int i = Annotations.Count - 1; i >= 0; i--)
            {
                if (HitBody(Annotations[i], pos))
                {
                    Cursor = CursorHand;
                    return;
                }
            }
        }

        Cursor = fallback;
    }

    // ════════════════════════════════════════
    //  HELPERS
    // ════════════════════════════════════════

    private static Rect ScreenRect(Annotation a) =>
        new(a.X * Scale, a.Y * Scale, a.WidthPt * Scale, a.HeightPt * Scale);

    private static Point RotatePoint(Point p, Point center, double radians)
    {
        double dx = p.X - center.X, dy = p.Y - center.Y;
        return new Point(
            center.X + dx * Math.Cos(radians) - dy * Math.Sin(radians),
            center.Y + dx * Math.Sin(radians) + dy * Math.Cos(radians));
    }

    private static double Dist(Point a, Point b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static FormattedText MakeFormattedText(TextAnnotation t)
    {
        var typeface = TextAnnotation.TypefaceCache.GetOrAdd(t.FontFamily,
            ff => new Typeface(TextAnnotation.MapFontForMeasure(ff)));
        return new FormattedText(
            t.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, t.FontSize * Scale, Brushes.Black);
    }
}
