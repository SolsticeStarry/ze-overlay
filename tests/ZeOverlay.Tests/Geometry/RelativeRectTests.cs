
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

/// <summary>
/// PLAN 第 9 节「自动跟随」的核心：ROI 必须能随目标窗口移动/缩放按比例重映射。
/// </summary>
public class RelativeRectTests
{
    [Fact]
    public void FromAbsolute_ToAbsolute_RoundTripsInsideWindow()
    {
        var window = new PixelRect(100, 50, 1920, 1080);
        var roi = new PixelRect(600, 300, 700, 400);

        RelativeRect relative = RelativeRect.FromAbsolute(roi, window);

        // 允许 1px 取整误差。
        Assert.True(roi.SimilarTo(relative.ToAbsolute(window), 1));
    }

    [Fact]
    public void ToAbsolute_FollowsWindowMove()
    {
        var window = new PixelRect(0, 0, 1000, 1000);
        var roi = new PixelRect(100, 200, 400, 300);
        RelativeRect relative = RelativeRect.FromAbsolute(roi, window);

        var moved = new PixelRect(300, 400, 1000, 1000);

        Assert.Equal(new PixelRect(400, 600, 400, 300), relative.ToAbsolute(moved));
    }

    [Fact]
    public void ToAbsolute_FollowsWindowResize()
    {
        var window = new PixelRect(0, 0, 1000, 1000);
        var roi = new PixelRect(100, 200, 400, 300);
        RelativeRect relative = RelativeRect.FromAbsolute(roi, window);

        // 窗口放大一倍：ROI 同步放大一倍。
        var resized = new PixelRect(0, 0, 2000, 2000);

        Assert.Equal(new PixelRect(200, 400, 800, 600), relative.ToAbsolute(resized));
    }

    [Fact]
    public void FromAbsolute_DegenerateWindow_ReturnsEmpty()
    {
        Assert.True(RelativeRect.FromAbsolute(new PixelRect(0, 0, 10, 10), PixelRect.Empty).IsEmpty);
    }

    [Fact]
    public void ToAbsolute_DegenerateWindow_ReturnsEmpty()
    {
        var relative = new RelativeRect(0.1, 0.2, 0.3, 0.4);

        Assert.True(relative.ToAbsolute(PixelRect.Empty).IsEmpty);
    }

    [Fact]
    public void ToAbsolute_NeverReturnsZeroSizedRect()
    {
        // 极小比例在缩小后的窗口上取整可能变成 0，必须钳到至少 1 像素。
        var relative = new RelativeRect(0.0, 0.0, 0.001, 0.001);
        PixelRect result = relative.ToAbsolute(new PixelRect(0, 0, 100, 100));

        Assert.True(result.Width >= 1);
        Assert.True(result.Height >= 1);
    }

    [Fact]
    public void ToString_Parse_RoundTripsWithInvariantCulture()
    {
        var relative = new RelativeRect(0.125, 0.25, 0.5, 0.75);

        Assert.Equal(relative, RelativeRect.Parse(relative.ToString()));
    }
}
