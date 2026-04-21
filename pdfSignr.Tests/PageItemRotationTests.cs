using pdfSignr.ViewModels;
using Xunit;

namespace pdfSignr.Tests;

public class PageItemRotationTests
{
    // A 100×40 annotation at (10, 20) on a 600×800 page.
    private const double AnnW = 100, AnnH = 40;
    private const double AnnX = 10, AnnY = 20;
    private const double PageW = 600, PageH = 800;

    [Fact]
    public void Zero_degree_rotation_is_identity()
    {
        var (x, y) = PageItem.RotatedPosition(0, AnnX, AnnY, AnnW, AnnH, PageW, PageH);
        Assert.Equal(AnnX, x);
        Assert.Equal(AnnY, y);
    }

    [Fact]
    public void NinetyDegrees_cw_maps_to_top_right_band()
    {
        // 90° CW: new X = oldH - y - h, new Y = x
        var (x, y) = PageItem.RotatedPosition(90, AnnX, AnnY, AnnW, AnnH, PageW, PageH);
        Assert.Equal(PageH - AnnY - AnnH, x); // 800 - 20 - 40 = 740
        Assert.Equal(AnnX, y);                 // 10
    }

    [Fact]
    public void OneEightyDegrees_flips_both_axes()
    {
        var (x, y) = PageItem.RotatedPosition(180, AnnX, AnnY, AnnW, AnnH, PageW, PageH);
        Assert.Equal(PageW - AnnX - AnnW, x); // 600 - 10 - 100 = 490
        Assert.Equal(PageH - AnnY - AnnH, y); // 800 - 20 -  40 = 740
    }

    [Fact]
    public void TwoSeventyDegrees_ccw_maps_to_bottom_left_band()
    {
        var (x, y) = PageItem.RotatedPosition(270, AnnX, AnnY, AnnW, AnnH, PageW, PageH);
        Assert.Equal(AnnY, x);                 // 20
        Assert.Equal(PageW - AnnX - AnnW, y); // 600 - 10 - 100 = 490
    }

    [Fact]
    public void Four_rotations_ninety_cw_returns_to_origin()
    {
        double x = AnnX, y = AnnY, w = AnnW, h = AnnH, pw = PageW, ph = PageH;
        for (int i = 0; i < 4; i++)
        {
            (x, y) = PageItem.RotatedPosition(90, x, y, w, h, pw, ph);
            (w, h) = (h, w);
            (pw, ph) = (ph, pw);
        }
        Assert.Equal(AnnX, x, precision: 9);
        Assert.Equal(AnnY, y, precision: 9);
    }
}
