using Avalonia;
using pdfSignr.Models;
using pdfSignr.Views;
using Xunit;

namespace pdfSignr.Tests;

public class AnnotationManipulatorTests
{
    private const double Scale = 1.0; // work in screen == PDF points for simplicity

    private static TextAnnotation MakeText(double x = 100, double y = 100, double w = 100, double h = 40)
        => new() { X = x, Y = y, WidthPt = w, HeightPt = h, Text = "t", FontFamily = "" };

    private static SvgAnnotation MakeSvg(double x = 100, double y = 100, double w = 100, double h = 40)
        => new()
        {
            X = x, Y = y, WidthPt = w, HeightPt = h,
            OriginalWidthPt = w, OriginalHeightPt = h, Scale = 1.0,
            SvgFilePath = "stub.svg", IsRaster = true,
        };

    // ───── Drag ─────

    [Fact]
    public void Drag_translates_by_screen_delta_divided_by_scale()
    {
        var m = new AnnotationManipulator();
        var ann = MakeText();
        m.BeginDrag(ann, new Point(200, 200));
        m.ProcessMove(new Point(225, 215), Scale);
        Assert.Equal(125, ann.X);
        Assert.Equal(115, ann.Y);
    }

    [Fact]
    public void Drag_records_start_state_for_undo()
    {
        var m = new AnnotationManipulator();
        var ann = MakeText(50, 60, 100, 40);
        m.BeginDrag(ann, new Point(200, 200));
        Assert.Equal(50, m.StartX);
        Assert.Equal(60, m.StartY);
        Assert.Equal(100, m.StartW);
        Assert.Equal(40, m.StartH);
        Assert.Equal(AnnotationManipulator.Mode.Dragging, m.State);
    }

    // ───── Resize (free) ─────

    [Fact]
    public void Resize_BR_grows_width_and_height()
    {
        var m = new AnnotationManipulator();
        var ann = MakeText(100, 100, 100, 40);
        m.BeginHandle(ann, AnnotationHandle.BR, new Point(200, 140), Scale);
        m.ProcessMove(new Point(260, 180), Scale);
        Assert.Equal(160, ann.WidthPt);
        Assert.Equal(80, ann.HeightPt);
        Assert.Equal(100, ann.X);
        Assert.Equal(100, ann.Y);
    }

    [Fact]
    public void Resize_TL_shrinks_and_shifts_origin()
    {
        var m = new AnnotationManipulator();
        var ann = MakeText(100, 100, 100, 40);
        m.BeginHandle(ann, AnnotationHandle.TL, new Point(100, 100), Scale);
        m.ProcessMove(new Point(130, 120), Scale);
        Assert.Equal(70, ann.WidthPt);
        Assert.Equal(20, ann.HeightPt);
        Assert.Equal(130, ann.X);
        Assert.Equal(120, ann.Y);
    }

    [Fact]
    public void Resize_respects_minimum_size()
    {
        var m = new AnnotationManipulator();
        var ann = MakeText(100, 100, 100, 40);
        m.BeginHandle(ann, AnnotationHandle.BR, new Point(200, 140), Scale);
        m.ProcessMove(new Point(0, 0), Scale); // drag far past top-left
        Assert.True(ann.WidthPt >= 1);
        Assert.True(ann.HeightPt >= 1);
    }

    // ───── Resize (SVG aspect lock) ─────

    [Fact]
    public void Resize_SvgAnnotation_BR_keeps_aspect_ratio()
    {
        var m = new AnnotationManipulator();
        var ann = MakeSvg(100, 100, 100, 40);
        m.BeginHandle(ann, AnnotationHandle.BR, new Point(200, 140), Scale);
        m.ProcessMove(new Point(260, 164), Scale); // along diagonal
        // Aspect should be preserved within tolerance (starting aspect 100/40 = 2.5)
        Assert.Equal(ann.WidthPt / ann.HeightPt, 100.0 / 40.0, precision: 6);
    }

    // ───── Rotate ─────

    [Fact]
    public void Rotate_tracks_pointer_angle_relative_to_center()
    {
        var m = new AnnotationManipulator();
        // Center at (150, 120); start angle from (250, 120) is 0 rad (east of center).
        var ann = MakeText(100, 100, 100, 40);
        m.BeginHandle(ann, AnnotationHandle.Rotate, new Point(250, 120), Scale);
        m.ProcessMove(new Point(150, 220), Scale); // south of center = +90° in screen Y-down
        Assert.Equal(90, ann.Rotation, precision: 4);
    }

    // ───── Reset ─────

    [Fact]
    public void Reset_returns_target_and_goes_idle()
    {
        var m = new AnnotationManipulator();
        var ann = MakeText();
        m.BeginDrag(ann, new Point(10, 10));
        var prev = m.Reset();
        Assert.Same(ann, prev);
        Assert.Null(m.Target);
        Assert.Equal(AnnotationManipulator.Mode.Idle, m.State);
    }

    [Fact]
    public void ProcessMove_when_idle_is_noop()
    {
        var m = new AnnotationManipulator();
        // No target, no target access — should not throw.
        m.ProcessMove(new Point(10, 10), Scale);
        Assert.True(m.IsIdle);
    }
}
