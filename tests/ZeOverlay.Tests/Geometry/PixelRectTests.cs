
using ZeOverlay.Shared;
using ZeOverlay.Infrastructure;
using ZeOverlay.Win32;
using ZeOverlay.Stage.ScreenCapture;
using ZeOverlay.Stage.RowAnalysis;
using ZeOverlay.Stage.GlyphSegmentation;
using ZeOverlay.Stage.Recognition;
using ZeOverlay.Stage.Parsing;
using ZeOverlay.Stage.Tracking;
using ZeOverlay.Stage.Matching;

namespace ZeOverlay.Tests;

public class PixelRectTests
{
    [Fact]
    public void ToString_Parse_RoundTrips()
    {
        var rect = new PixelRect(10, 20, 300, 400);

        Assert.Equal("10,20,300,400", rect.ToString());
        Assert.Equal(rect, PixelRect.Parse(rect.ToString()));
    }

    [Fact]
    public void Parse_EmptyText_ReturnsEmpty()
    {
        Assert.True(PixelRect.Parse(null).IsEmpty);
        Assert.True(PixelRect.Parse("   ").IsEmpty);
    }

    [Fact]
    public void Parse_InvalidFormat_Throws()
    {
        Assert.Throws<FormatException>(() => PixelRect.Parse("1,2,3"));
    }

    [Fact]
    public void Normalized_HandlesReverseDrag()
    {
        // 从右下往左上拖：宽高为负。
        var reverse = new PixelRect(200, 150, -80, -50);

        Assert.Equal(new PixelRect(120, 100, 80, 50), reverse.Normalized());
    }

    [Fact]
    public void Intersect_DisjointRects_ReturnsEmpty()
    {
        var a = new PixelRect(0, 0, 10, 10);
        var b = new PixelRect(50, 50, 10, 10);

        Assert.True(a.Intersect(b).IsEmpty);
    }

    [Fact]
    public void Intersect_Overlapping_ClipsToOverlap()
    {
        var a = new PixelRect(0, 0, 100, 100);
        var b = new PixelRect(50, 60, 100, 100);

        Assert.Equal(new PixelRect(50, 60, 50, 40), a.Intersect(b));
    }

    [Fact]
    public void Contains_UsesHalfOpenBounds()
    {
        var rect = new PixelRect(10, 10, 10, 10);

        Assert.True(rect.Contains(10, 10));
        Assert.True(rect.Contains(19, 19));
        Assert.False(rect.Contains(20, 20));
        Assert.False(rect.Contains(9, 10));
    }

    [Fact]
    public void SimilarTo_WithinTolerance_IsTrue()
    {
        var a = new PixelRect(100, 100, 500, 300);
        var b = new PixelRect(102, 98, 501, 299);

        Assert.True(a.SimilarTo(b, 3));
        Assert.False(a.SimilarTo(b, 1));
    }

    [Fact]
    public void Area_EmptyRect_IsZero()
    {
        Assert.Equal(0, PixelRect.Empty.Area);
        Assert.Equal(0, new PixelRect(5, 5, -1, 10).Area);
    }
}
