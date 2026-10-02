using ZeOverlay.Shared;
using ZeOverlay.Stage.Tracking;

namespace ZeOverlay.Tests.Tracking;

/// <summary>
/// 社区服实测**无翻页**，行数波动不能产生第 2 页身份（否则会凭空多出一批条目）。
/// </summary>
public sealed class PagingDisabledTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PagingDisabled_RowDropStaysPageOne()
    {
        var tracker = new Tracker(new TrackerOptions { PagingEnabled = false, MaxRowsPerPage = 16 });
        tracker.Observe(13, [new ObservedRow(0, "手电", ArtifactState.Ready)], T0);

        TrackerFrameResult result = tracker.Observe(1, [new ObservedRow(0, "手电", ArtifactState.Ready)], T0.AddSeconds(2));

        Assert.Equal(VisiblePage.Page1, result.Page);
        Assert.All(tracker.Snapshot(T0.AddSeconds(2)), e => Assert.Equal(VisiblePage.Page1, e.Page));
    }

    [Fact]
    public void PagingEnabled_RowDropBecomesPageTwo()
    {
        var tracker = new Tracker(new TrackerOptions { PagingEnabled = true });
        tracker.Observe(12, [new ObservedRow(0, "滋水枪", ArtifactState.Ready)], T0);

        TrackerFrameResult result = tracker.Observe(1, [new ObservedRow(0, "别的神器", ArtifactState.Ready)], T0.AddSeconds(2));

        Assert.Equal(VisiblePage.Page2, result.Page);
    }

    [Fact]
    public void PagingEnabled_UsesConfiguredMaxRowsPerPageAsBaseline()
    {
        // 上限 16 时基准可到 16：9 行 < 16×0.6=10 ⇒ 判为第 2 页。
        // 若仍硬编 12，阈值只有 8，9 行会被误判成第 1 页。
        var tracker = new Tracker(new TrackerOptions { PagingEnabled = true, MaxRowsPerPage = 16 });
        tracker.Observe(16, [new ObservedRow(0, "手电", ArtifactState.Ready)], T0);

        Assert.Equal(VisiblePage.Page2, tracker.Observe(9, [new ObservedRow(0, "别的神器", ArtifactState.Ready)], T0.AddSeconds(1)).Page);
    }
}
