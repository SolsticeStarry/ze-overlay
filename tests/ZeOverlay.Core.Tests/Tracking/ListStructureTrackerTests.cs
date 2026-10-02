using ZeOverlay.Core.Analysis;
using ZeOverlay.Core.Tracking;

namespace ZeOverlay.Core.Tests.Tracking;

/// <summary>
/// 翻页判据。依据见 `docs/REAL_SAMPLES.md`：翻页没有视觉信号，
/// 只能靠「内容变化 + 行数骤降」检测；第 2 页行数显著少于第 1 页。
/// </summary>
public class ListStructureTrackerTests
{
    [Fact]
    public void FirstObservation_ReturnsNullAndEstablishesBaseline()
    {
        var tracker = new ListStructureTracker();

        ListChange? change = tracker.Observe(Snapshot(11, "a|b|c"));

        Assert.Null(change);
        Assert.True(tracker.HasBaseline);
        Assert.Equal(11, tracker.BaselineRowCount);
    }

    [Fact]
    public void IdenticalFrame_ReturnsNull()
    {
        var tracker = new ListStructureTracker();
        tracker.Observe(Snapshot(11, "a|b|c"));

        Assert.Null(tracker.Observe(Snapshot(11, "a|b|c")));
    }

    [Fact]
    public void RowCountDropToSecondPageScale_LooksLikePageChange()
    {
        var tracker = new ListStructureTracker();
        tracker.Observe(Snapshot(11, "a|b|c"));

        // 实测结论：第 2 页行数显著少。11 → 4 应判为疑似翻页。
        ListChange? change = tracker.Observe(Snapshot(4, "x|y"));

        Assert.NotNull(change);
        Assert.True(change.LooksLikePageChange);
        Assert.Equal(ListChangeKind.RowCountAndContentChanged, change.Kind);
        Assert.Equal(11, change.PreviousRowCount);
        Assert.Equal(4, change.CurrentRowCount);

        // 基准行数不应被第 2 页拉低，否则后续判断会失真。
        Assert.Equal(11, tracker.BaselineRowCount);
    }

    [Fact]
    public void SmallRowCountChange_IsNotPageChange()
    {
        var tracker = new ListStructureTracker();
        tracker.Observe(Snapshot(11, "a|b|c"));

        // 玩家消耗掉 1 个神器：11 → 10，仍在第 1 页量级。
        ListChange? change = tracker.Observe(Snapshot(10, "b|c"));

        Assert.NotNull(change);
        Assert.False(change.LooksLikePageChange);
    }

    [Fact]
    public void ContentChangeWithSameRowCount_IsNotPageChange()
    {
        var tracker = new ListStructureTracker();
        tracker.Observe(Snapshot(11, "a|b|c"));

        ListChange? change = tracker.Observe(Snapshot(11, "a|b|d"));

        Assert.NotNull(change);
        Assert.Equal(ListChangeKind.ContentChanged, change.Kind);
        Assert.False(change.LooksLikePageChange);
    }

    [Fact]
    public void ReturningToFirstPage_IsNotPageChange()
    {
        var tracker = new ListStructureTracker();
        tracker.Observe(Snapshot(11, "a|b|c"));
        tracker.Observe(Snapshot(4, "x|y"));

        // 翻回第 1 页是**上升**方向，不应被判成「翻到第 2 页」。
        ListChange? change = tracker.Observe(Snapshot(11, "a|b|c"));

        Assert.NotNull(change);
        Assert.False(change.LooksLikePageChange);
        Assert.Equal(11, tracker.BaselineRowCount);
    }

    [Fact]
    public void BaselineTracksLargestRowCountSeen()
    {
        var tracker = new ListStructureTracker();
        tracker.Observe(Snapshot(6, "a"));
        tracker.Observe(Snapshot(11, "a|b|c"));

        Assert.Equal(11, tracker.BaselineRowCount);
    }

    private static ListStructureSnapshot Snapshot(int rowCount, string signature)
        => new(rowCount, 30, signature);

    [Fact]
    public void BuildSignature_QuantizesAwayAntialiasingJitter()
    {
        // 实测：不量化时同一画面相邻帧的带宽会在 ±2 px 抖动，Top 也会 ±1，
        // 导致每帧都被判成「内容变化」。签名必须对这类抖动免疫。
        RowBand[] a = [new(0, 4, 20, 10, 228, 1, 1), new(1, 98, 114, 10, 62, 1, 1)];
        RowBand[] b = [new(0, 4, 20, 10, 230, 1, 1), new(1, 99, 115, 10, 62, 1, 1)];

        Assert.Equal(ListStructureSnapshot.BuildSignature(a), ListStructureSnapshot.BuildSignature(b));
    }

    [Fact]
    public void BuildSignature_DistinguishesRealContentChange()
    {
        RowBand[] a = [new(0, 4, 20, 10, 228, 1, 1)];
        RowBand[] b = [new(0, 4, 20, 10, 120, 1, 1)];   // 名称明显更短

        Assert.NotEqual(ListStructureSnapshot.BuildSignature(a), ListStructureSnapshot.BuildSignature(b));
    }

    [Fact]
    public void FromBands_CarriesRowCountAndPitch()
    {
        RowBand[] bands = [new(0, 4, 20, 10, 228, 1, 1), new(1, 34, 50, 10, 200, 1, 1)];

        ListStructureSnapshot snapshot = ListStructureSnapshot.FromBands(bands, 30);

        Assert.Equal(2, snapshot.RowCount);
        Assert.Equal(30, snapshot.MedianPitch);
    }
}
