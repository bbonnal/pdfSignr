using Avalonia;
using pdfSignr.Models;
using static pdfSignr.Views.InteractionConstants;

namespace pdfSignr.Views;

/// <summary>
/// Pointer-driven state machine for manipulating a single annotation (drag, resize, rotate).
/// Pure — no Avalonia control dependency beyond <see cref="Point"/>. PageCanvas owns the
/// pointer capture and event plumbing; this class owns the math and snapshot state for undo.
/// </summary>
internal sealed class AnnotationManipulator
{
    public enum Mode { Idle, Dragging, Resizing, Rotating }

    public Mode State { get; private set; } = Mode.Idle;
    public Annotation? Target { get; private set; }
    public AnnotationHandle ActiveHandle { get; private set; } = AnnotationHandle.None;

    // Start snapshot — used to compute deltas and to report old values on release for undo.
    public double StartX { get; private set; }
    public double StartY { get; private set; }
    public double StartW { get; private set; }
    public double StartH { get; private set; }
    public double StartRot { get; private set; }
    private Point _dragStart;
    private double _startAngle;

    public bool IsIdle => State == Mode.Idle;

    public void BeginDrag(Annotation ann, Point pos)
    {
        Capture(ann, pos);
        State = Mode.Dragging;
        ActiveHandle = AnnotationHandle.None;
    }

    public void BeginHandle(Annotation ann, AnnotationHandle handle, Point pos, double scale)
    {
        Capture(ann, pos);
        ActiveHandle = handle;
        if (handle == AnnotationHandle.Rotate)
        {
            State = Mode.Rotating;
            var rect = AnnotationHitTester.ScreenRect(ann, scale);
            var c = rect.Center;
            _startAngle = Math.Atan2(pos.Y - c.Y, pos.X - c.X);
        }
        else
        {
            State = Mode.Resizing;
        }
    }

    /// <summary>Applies the current pointer position to the target annotation per mode.</summary>
    public void ProcessMove(Point pos, double scale)
    {
        if (Target == null) return;
        switch (State)
        {
            case Mode.Dragging:
                Target.X = StartX + (pos.X - _dragStart.X) / scale;
                Target.Y = StartY + (pos.Y - _dragStart.Y) / scale;
                break;
            case Mode.Resizing:
                Resize(pos, scale);
                break;
            case Mode.Rotating:
                Rotate(pos, scale);
                break;
        }
    }

    /// <summary>Returns the previous target (for event reporting) and resets state.</summary>
    public Annotation? Reset()
    {
        var t = Target;
        Target = null;
        ActiveHandle = AnnotationHandle.None;
        State = Mode.Idle;
        return t;
    }

    private void Capture(Annotation ann, Point pos)
    {
        Target = ann;
        _dragStart = pos;
        StartX = ann.X;
        StartY = ann.Y;
        StartW = ann.WidthPt;
        StartH = ann.HeightPt;
        StartRot = ann.Rotation;
    }

    private void Resize(Point pos, double scale)
    {
        if (Target == null) return;

        double rawDx = pos.X - _dragStart.X;
        double rawDy = pos.Y - _dragStart.Y;

        if (Target.Rotation != 0)
        {
            double a = -Target.Rotation * Math.PI / 180.0;
            double cos = Math.Cos(a), sin = Math.Sin(a);
            (rawDx, rawDy) = (rawDx * cos - rawDy * sin, rawDx * sin + rawDy * cos);
        }

        double dx = rawDx / scale;
        double dy = rawDy / scale;

        double sx = ActiveHandle is AnnotationHandle.TL or AnnotationHandle.BL ? -1 : 1;
        double sy = ActiveHandle is AnnotationHandle.TL or AnnotationHandle.TR ? -1 : 1;

        double newW, newH;

        if (Target is SvgAnnotation && StartW > 0 && StartH > 0)
        {
            // Aspect-locked diagonal scaling for signatures — a drag along the corner
            // direction enlarges uniformly; perpendicular movement does not squash the graphic.
            double origDiag = Math.Sqrt(StartW * StartW + StartH * StartH);
            double diagDirX = sx * StartW / origDiag;
            double diagDirY = sy * StartH / origDiag;
            double proj = dx * diagDirX + dy * diagDirY;
            double ratio = Math.Max(MinSizePt / Math.Min(StartW, StartH),
                                    (origDiag + proj) / origDiag);
            newW = StartW * ratio;
            newH = StartH * ratio;
        }
        else
        {
            newW = Math.Max(MinSizePt, StartW + sx * dx);
            newH = Math.Max(MinSizePt, StartH + sy * dy);
        }

        Target.WidthPt = newW;
        Target.HeightPt = newH;
        Target.X = sx < 0 ? StartX + StartW - newW : StartX;
        Target.Y = sy < 0 ? StartY + StartH - newH : StartY;
    }

    private void Rotate(Point pos, double scale)
    {
        if (Target == null) return;
        var rect = AnnotationHitTester.ScreenRect(Target, scale);
        var c = rect.Center;
        double current = Math.Atan2(pos.Y - c.Y, pos.X - c.X);
        double delta = (current - _startAngle) * 180.0 / Math.PI;
        Target.Rotation = StartRot + delta;
    }
}
