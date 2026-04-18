using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using pdfSignr.Models;
using static pdfSignr.Views.InteractionConstants;

namespace pdfSignr.Views;

/// <summary>
/// Stateless drawing for a page bitmap, its annotations, and selection chrome.
/// The renderer has no knowledge of pointer state — it renders what it is told.
/// </summary>
internal static class AnnotationRenderer
{
    // Shared cached pens (pure draw data — safe to share across all renders)
    private static readonly Pen SelectionDashPen = new(Brushes.DodgerBlue, 1.5, DashStyle.Dash);
    private static readonly Pen HandlePen = new(Brushes.DodgerBlue, 1.5);
    private static readonly Pen DeleteCirclePen = new(Brushes.IndianRed, 1.5);
    private static readonly Pen DeleteXPen = new(Brushes.IndianRed, 2);

    public static void DrawPage(DrawingContext ctx, Bitmap? pageBitmap,
        double pageWidthPt, double pageHeightPt, double scale)
    {
        if (pageBitmap == null) return;

        var destRect = new Rect(0, 0, pageWidthPt * scale, pageHeightPt * scale);
        if (destRect.Width <= 0 || destRect.Height <= 0)
            destRect = new Rect(0, 0, pageBitmap.Size.Width, pageBitmap.Size.Height);
        ctx.DrawImage(pageBitmap, destRect);
    }

    public static void DrawAnnotations(DrawingContext ctx, IEnumerable<Annotation>? annotations, double scale)
    {
        if (annotations == null) return;

        foreach (var ann in annotations)
        {
            var rect = AnnotationHitTester.ScreenRect(ann, scale);
            if (ann.Rotation != 0)
            {
                var c = rect.Center;
                using (ctx.PushTransform(
                    Matrix.CreateTranslation(-c.X, -c.Y) *
                    Matrix.CreateRotation(ann.Rotation * Math.PI / 180.0) *
                    Matrix.CreateTranslation(c.X, c.Y)))
                {
                    DrawContent(ctx, ann, rect, scale);
                    if (ann.IsSelected) DrawChrome(ctx, rect);
                }
            }
            else
            {
                DrawContent(ctx, ann, rect, scale);
                if (ann.IsSelected) DrawChrome(ctx, rect);
            }
        }
    }

    private static void DrawContent(DrawingContext ctx, Annotation ann, Rect rect, double scale)
    {
        if (ann is TextAnnotation t)
        {
            var ft = MakeFormattedText(t, scale);
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

        DrawCircle(ctx, rect.TopLeft, HandleRadius, Brushes.White, HandlePen);
        DrawCircle(ctx, rect.TopRight, HandleRadius, Brushes.White, HandlePen);
        DrawCircle(ctx, rect.BottomLeft, HandleRadius, Brushes.White, HandlePen);
        DrawCircle(ctx, rect.BottomRight, HandleRadius, Brushes.White, HandlePen);

        var topMid = new Point(rect.Center.X, rect.Top);
        var rotPos = new Point(rect.Center.X, rect.Top - RotateDistance);
        ctx.DrawLine(HandlePen, topMid, rotPos);
        DrawCircle(ctx, rotPos, RotateRadius, Brushes.LightGreen, HandlePen);

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

    private static FormattedText MakeFormattedText(TextAnnotation t, double scale)
    {
        var typeface = TextAnnotation.TypefaceCache.GetOrAdd(t.FontFamily,
            ff => new Typeface(TextAnnotation.MapFontForMeasure(ff)));
        return new FormattedText(
            t.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, t.FontSize * scale, Brushes.Black);
    }
}
