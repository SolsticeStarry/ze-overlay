
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
/// 行网格拟合。依据实测：列表行左对齐且**严格等距**（样本 #1 为 30 px），
/// 因此网格之外的强梯度（场景亮物件、窗口边缘）应当被判为离群并剔除。
/// </summary>
public class RowGridEstimatorTests
{
    [Fact]
    public void UniformGrid_NoOutliers()
    {
        RowBand[] bands = [Band(100), Band(130), Band(160), Band(190)];

        RowGrid? grid = Grid.Estimate(bands, 400);

        Assert.NotNull(grid);
        Assert.Equal(30, grid.Pitch, 3);
        Assert.Equal(4, grid.InlierCount);
        Assert.Empty(grid.OutlierBandIndices);
        Assert.Equal(4, grid.SlotCount);
    }

    [Fact]
    public void SingleOutlierBand_IsRejected()
    {
        // 4 行规律列表 + 1 个远处的噪声行带（模拟场景亮物件/窗口边缘）
        RowBand[] bands = [Band(100), Band(130), Band(160), Band(190), Band(300)];

        RowGrid? grid = Grid.Estimate(bands, 400);

        Assert.NotNull(grid);
        Assert.Equal(30, grid.Pitch, 3);
        Assert.Equal(4, grid.InlierCount);
        Assert.Equal([4], grid.OutlierBandIndices);
        Assert.Equal(0.8, grid.InlierRatio, 3);
    }

    [Fact]
    public void PrefersLargerPitch_WhenSubMultipleTies()
    {
        // 步长 15 是 30 的约数，会拟合出同一组内点；应选真步长（较大的那个）。
        RowBand[] bands = [Band(100), Band(130), Band(160), Band(190)];

        RowGrid? grid = Grid.Estimate(bands, 400);

        Assert.NotNull(grid);
        Assert.Equal(30, grid.Pitch, 3);
    }

    [Fact]
    public void MissingRow_IsReportedAsMissingSlot()
    {
        RowBand[] bands = [Band(100), Band(130), Band(190)];   // 缺 160 那一行

        RowGrid? grid = Grid.Estimate(bands, 400);

        Assert.NotNull(grid);
        Assert.Equal(30, grid.Pitch, 3);
        Assert.Equal(3, grid.InlierCount);
        Assert.Equal(4, grid.SlotCount);
        Assert.Contains(2, grid.MissingSlots);
    }

    [Fact]
    public void SlotOf_SnapsWithinToleranceAndRejectsOutside()
    {
        RowBand[] bands = [Band(100), Band(130), Band(160)];

        RowGrid? grid = Grid.Estimate(bands, 400);

        Assert.NotNull(grid);
        Assert.Equal(1, grid.SlotOf(133));    // 130±3，容差 7.5
        Assert.Null(grid.SlotOf(145));        // 落在两槽中间
    }

    [Fact]
    public void FewerThanMinRows_ReturnsNull()
    {
        Assert.Null(Grid.Estimate([Band(100), Band(130)], 400));
        Assert.Null(Grid.Estimate([], 400));
    }

    [Fact]
    public void PitchBelowPhysicalFloor_ReturnsNull()
    {
        // 行距不可能小于字高（实测 15–18 px）。间距 8 px 的「行」不可能是行网格。
        RowBand[] tooTight = [Band(100), Band(108), Band(116), Band(124)];

        Assert.Null(Grid.Estimate(tooTight, 400));
    }

    [Fact]
    public void CenterOf_ExtrapolatesGrid()
    {
        RowBand[] bands = [Band(100), Band(130), Band(160)];

        RowGrid grid = Grid.Estimate(bands, 400)!;

        Assert.Equal(190, grid.CenterOf(3), 3);
        Assert.Equal(70, grid.CenterOf(-1), 3);
    }

    /// <summary>构造一个中心在 <paramref name="centerY"/>、高 15 px 的行带（中心无小数）。</summary>
    private static RowBand Band(int centerY, int width = 200)
        => new(0, centerY - 7, centerY + 7, 100, 100 + width, 1000, 1200);
}
