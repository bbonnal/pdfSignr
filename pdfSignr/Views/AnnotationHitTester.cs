using Avalonia;
using pdfSignr.Models;
using static pdfSignr.Views.InteractionConstants;

namespace pdfSignr.Views;

internal enum AnnotationHandle { None, TL, TR, BL, BR, Rotate, Delete }

/// <summary>
/// Stateless hit-testing for annotation bodies and manipulation handles.
/// All coordinates are in screen space (scale-applied).
/// </summary>
internal static class AnnotationHitTester
{
    public static Rect ScreenRect(Annotation a, double scale) =>
        new(a.X * scale, a.Y * scale, a.WidthPt * scale, a.HeightPt * scale);

    public static AnnotationHandle HitHandle(Annotation ann, Point pos, double scale)
    {
        var rect = ScreenRect(ann, scale);
        var c = rect.Center;
        double angle = ann.Rotation * Math.PI / 180.0;

        Point R(Point p) => RotatePoint(p, c, angle);

        var delPos = R(new Point(rect.Right + DeleteOffset, rect.Top - DeleteOffset));
        if (Dist(pos, delPos) <= HandleHit) return AnnotationHandle.Delete;

        var rotPos = R(new Point(rect.Center.X, rect.Top - RotateDistance));
        if (Dist(pos, rotPos) <= HandleHit) return AnnotationHandle.Rotate;
        if (Dist(pos, R(rect.TopLeft)) <= HandleHit) return AnnotationHandle.TL;
        if (Dist(pos, R(rect.TopRight)) <= HandleHit) return AnnotationHandle.TR;
        if (Dist(pos, R(rect.BottomLeft)) <= HandleHit) return AnnotationHandle.BL;
        if (Dist(pos, R(rect.BottomRight)) <= HandleHit) return AnnotationHandle.BR;
        return AnnotationHandle.None;
    }

    public static bool HitBody(Annotation ann, Point pos, double scale)
    {
        var rect = ScreenRect(ann, scale);
        if (ann.Rotation != 0)
        {
            var local = RotatePoint(pos, rect.Center, -ann.Rotation * Math.PI / 180.0);
            return rect.Inflate(HitBodyInflate).Contains(local);
        }
        return rect.Inflate(HitBodyInflate).Contains(pos);
    }

    public static Point RotatePoint(Point p, Point center, double radians)
    {
        double dx = p.X - center.X, dy = p.Y - center.Y;
        return new Point(
            center.X + dx * Math.Cos(radians) - dy * Math.Sin(radians),
            center.Y + dx * Math.Sin(radians) + dy * Math.Cos(radians));
    }

    public static double Dist(Point a, Point b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
