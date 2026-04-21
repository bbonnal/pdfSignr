using pdfSignr.Views;
using Xunit;

namespace pdfSignr.Tests;

public class ViewportPolicyTests
{
    // ───── QuantizeDpi ─────

    [Theory]
    [InlineData(72, 96)]
    [InlineData(96, 96)]
    [InlineData(120, 150)]
    [InlineData(200, 200)]
    [InlineData(300, 300)]
    [InlineData(450, 400)]
    [InlineData(700, 600)]
    [InlineData(800, 800)]
    [InlineData(1500, 1200)]
    public void QuantizeDpi_snaps_to_ladder_without_upper_cap(int requested, int expected)
    {
        Assert.Equal(expected, ViewportPolicy.QuantizeDpi(requested));
    }

    [Fact]
    public void QuantizeDpi_is_monotonic()
    {
        int prev = ViewportPolicy.QuantizeDpi(0);
        for (int i = 1; i < 2000; i += 7)
        {
            int cur = ViewportPolicy.QuantizeDpi(i);
            Assert.True(cur >= prev, $"monotonicity broke at {i}: {prev} → {cur}");
            prev = cur;
        }
    }

    // ───── ClampDpiForPage ─────

    [Fact]
    public void ClampDpiForPage_returns_requested_dpi_for_reasonable_pages()
    {
        // A 612×792 page at 300 DPI is well within the 256 MB cap.
        Assert.Equal(300, ViewportPolicy.ClampDpiForPage(300, 612, 792));
    }

    [Fact]
    public void ClampDpiForPage_caps_dpi_for_gigantic_pages_at_extreme_zoom()
    {
        // Huge 8000×6000 pt "page" at 1200 DPI would be ~80 GB of pixels.
        int result = ViewportPolicy.ClampDpiForPage(1200, 8000, 6000);
        Assert.True(result < 1200, $"expected a lower clamped value, got {result}");
        Assert.True(result >= 72);
    }

    [Fact]
    public void ClampDpiForPage_never_returns_below_72()
    {
        Assert.Equal(72, ViewportPolicy.ClampDpiForPage(72, 1000000, 1000000));
    }

    [Fact]
    public void ClampDpiForPage_returns_requested_when_area_is_zero()
    {
        Assert.Equal(300, ViewportPolicy.ClampDpiForPage(300, 0, 792));
    }

    // ───── VisibleIndices ─────

    [Fact]
    public void VisibleIndices_returns_pages_inside_viewport()
    {
        var tops = new double[] { 0, 800, 1600, 2400, 3200 };
        var heights = new double[] { 800, 800, 800, 800, 800 };
        var visible = ViewportPolicy.VisibleIndices(tops, heights, 900, 1000).ToHashSet();
        Assert.Contains(1, visible); // 800–1600
        Assert.Contains(2, visible); // 1600–2400
        Assert.DoesNotContain(0, visible);
        Assert.DoesNotContain(3, visible);
    }

    [Fact]
    public void VisibleIndices_includes_partially_visible_edges()
    {
        var tops = new double[] { 0, 800, 1600 };
        var heights = new double[] { 800, 800, 800 };
        var visible = ViewportPolicy.VisibleIndices(tops, heights, 750, 100).ToHashSet();
        Assert.Contains(0, visible); // top edge sticks into viewport
        Assert.Contains(1, visible); // viewport covers start of page 1
    }

    [Fact]
    public void VisibleIndices_empty_for_empty_document()
    {
        Assert.Empty(ViewportPolicy.VisibleIndices(
            Array.Empty<double>(), Array.Empty<double>(), 0, 1000));
    }

    [Fact]
    public void VisibleIndices_uses_binary_search_start_O_logn()
    {
        // Exercises the binary-search branch on a 10K-page document. Pick a scrollY
        // inside page 6250 (its top is at 5,000,000) so the visible span is unambiguous.
        var tops = Enumerable.Range(0, 10_000).Select(i => (double)(i * 800)).ToArray();
        var heights = Enumerable.Range(0, 10_000).Select(_ => 800.0).ToArray();
        var visible = ViewportPolicy.VisibleIndices(tops, heights, 5_000_100, 900).ToHashSet();
        // scrollY=5,000,100 lands 100 px into page 6250; viewport=900 px so bottom=5,001,000
        // reaches 200 px into page 6251.
        Assert.Equal(new HashSet<int> { 6250, 6251 }, visible);
    }

    // ───── ExpandRing ─────

    [Fact]
    public void ExpandRing_clamps_to_page_count_bounds()
    {
        Assert.Equal((0, 5), ViewportPolicy.ExpandRing(2, 2, radius: 3, pageCount: 6));
        Assert.Equal((0, 2), ViewportPolicy.ExpandRing(0, 0, radius: 5, pageCount: 3));
    }

    [Fact]
    public void ExpandRing_preserves_radius_in_interior()
    {
        Assert.Equal((47, 63), ViewportPolicy.ExpandRing(50, 60, radius: 3, pageCount: 100));
    }
}
