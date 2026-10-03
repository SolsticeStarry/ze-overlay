
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
/// 按**行槽位**跟踪。身份 = `(页, 行号)`，不用神器名或玩家名。
/// 对应用户的实测反馈：神器会经常性换玩家，用名称+玩家名做 key 时单帧无法判断
/// 「旧条目更新」还是「全新条目」，且玩家名 OCR 错字会造成大量重复条目。
/// </summary>
public class ArtifactTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SameSlot_IsUpdatedInPlace()
    {
        var tracker = new Tracker();

        tracker.Observe(12, [Row(0, "滋水枪", ArtifactState.Cooling, 20)], T0);
        TrackerFrameResult result = tracker.Observe(12, [Row(0, "滋水枪", ArtifactState.Cooling, 18)], T0.AddSeconds(2));

        Assert.Empty(result.Added);
        Assert.Single(result.Refreshed);
        Assert.Equal(18, tracker.Snapshot(T0.AddSeconds(2))[0].CooldownSeconds);
    }

    [Fact]
    public void ArtifactSwitchingPlayer_DoesNotCreateNewEntry()
    {
        // 核心诉求：神器换玩家，行号没变 ⇒ 只更新这一行，不产生新条目
        var tracker = new Tracker();
        tracker.Observe(12, [Row(0, "滋水枪", ArtifactState.Cooling, 20)], T0);

        // 同一行现在显示的是「另一名玩家持有的同一把神器」——槽位模型下这没有区别
        TrackerFrameResult result = tracker.Observe(12, [Row(0, "滋水枪", ArtifactState.Ready)], T0.AddSeconds(1));

        Assert.Empty(result.Added);
        Assert.Single(tracker.Snapshot(T0.AddSeconds(1)));
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void DifferentSlots_AreDifferentEntries()
    {
        var tracker = new Tracker();

        tracker.Observe(12, [Row(0, "滋水枪"), Row(1, "荧光棒")], T0);

        Assert.Equal(2, tracker.Snapshot(T0).Count);
    }

    [Fact]
    public void RowContentReplaced_SlotKeepsIdentity()
    {
        // 同一行换了神器（原来的被用掉、下一个顶上）⇒ 槽位身份不变，内容被覆盖
        var tracker = new Tracker();
        tracker.Observe(12, [Row(3, "滋水枪")], T0);

        TrackerFrameResult result = tracker.Observe(12, [Row(3, "荧光棒")], T0.AddSeconds(2));

        Assert.Empty(result.Added);
        TrackerEntryView view = tracker.Snapshot(T0.AddSeconds(2))[0];
        Assert.Equal("荧光棒", view.ArtifactName);
        Assert.Equal(3, view.Slot);
    }

    [Fact]
    public void MissingOnVisiblePage_CountsOncePerTimeBucket()
    {
        var tracker = new Tracker();
        tracker.Observe(12, [Row(0, "滋水枪"), Row(1, "荧光棒")], T0);

        // 第 1 页仍可见但第 1 行读不到：1 秒内连采 10 帧，只应记 1 次 miss
        for (int i = 1; i <= 10; i++)
        {
            tracker.Observe(12, [Row(0, "滋水枪")], T0.AddMilliseconds(100 * i));
        }

        Assert.Equal(1, tracker.Snapshot(T0.AddSeconds(1)).Single(e => e.Slot == 1).MissedSessions);
    }

    [Fact]
    public void MissingOnVisiblePage_EventuallyRemovesSlot()
    {
        var tracker = new Tracker();
        tracker.Observe(12, [Row(5, "滋水枪")], T0);

        tracker.Observe(12, [], T0.AddSeconds(2));
        tracker.Observe(12, [], T0.AddSeconds(4));
        TrackerFrameResult result = tracker.Observe(12, [], T0.AddSeconds(7));

        Assert.Contains("滋水枪", result.Removed[0]);
        Assert.Empty(tracker.Snapshot(T0.AddSeconds(7)));
    }

    [Fact]
    public void PageNotVisible_AlsoDisappearsAfterTimeout()
    {
        var tracker = new Tracker();
        tracker.Observe(12, [Row(0, "滋水枪", ArtifactState.Cooling, 60)], T0);

        // 翻到第 2 页，停留 3 秒（未超时）：第 1 页槽位仍在（外推）
        for (int i = 1; i <= 6; i++)
        {
            tracker.Observe(1, [Row(0, "别的神器")], T0.AddMilliseconds(500 * i));
        }

        TrackerEntryView before = tracker.Snapshot(T0.AddSeconds(3)).Single(e => e.Page == VisiblePage.Page1);
        Assert.Equal(EntrySource.Extrapolated, before.Source);

        // 停留 7 秒（超过 6s 无条件超时）：第 1 页槽位消失
        for (int i = 7; i <= 14; i++)
        {
            tracker.Observe(1, [Row(0, "别的神器")], T0.AddMilliseconds(500 * i));
        }

        Assert.DoesNotContain(tracker.Snapshot(T0.AddSeconds(7)), e => e.Page == VisiblePage.Page1);
    }

    [Fact]
    public void SameSlotNumberOnDifferentPages_AreSeparateEntries()
    {
        var tracker = new Tracker();
        tracker.Observe(12, [Row(0, "第1页神器")], T0);
        tracker.Observe(1, [Row(0, "第2页神器")], T0.AddSeconds(2));

        IReadOnlyList<TrackerEntryView> view = tracker.Snapshot(T0.AddSeconds(2));
        Assert.Equal(2, view.Count);
        Assert.Contains(view, e => e.Page == VisiblePage.Page1 && e.ArtifactName == "第1页神器");
        Assert.Contains(view, e => e.Page == VisiblePage.Page2 && e.ArtifactName == "第2页神器");
    }

    [Fact]
    public void ExtrapolatedCountdown_ReachingZero_ShowsReady()
    {
        var tracker = new Tracker();
        tracker.Observe(12, [Row(0, "滋水枪", ArtifactState.Cooling, 3)], T0);
        tracker.Observe(1, [Row(0, "别的神器")], T0.AddSeconds(1));

        TrackerEntryView view = tracker.Snapshot(T0.AddSeconds(4)).Single(e => e.Page == VisiblePage.Page1);

        Assert.Equal(0, view.CooldownSeconds);
        Assert.Equal(ArtifactState.Ready, view.State);
        Assert.Equal(EntrySource.Extrapolated, view.Source);
    }

    [Fact]
    public void ListDisappears_AlsoDisappearsAfterTimeout()
    {
        var tracker = new Tracker();
        tracker.Observe(12, [Row(0, "滋水枪", ArtifactState.Cooling, 30)], T0);

        // 列表消失、停留 3 秒（未超时）：仍在外推
        for (int i = 1; i <= 6; i++)
        {
            tracker.Observe(0, [], T0.AddMilliseconds(500 * i));
        }

        TrackerEntryView view = tracker.Snapshot(T0.AddSeconds(3))[0];
        Assert.Equal(EntrySource.Extrapolated, view.Source);

        // 停留 7 秒（超过 6s）：消失
        for (int i = 7; i <= 14; i++)
        {
            tracker.Observe(0, [], T0.AddMilliseconds(500 * i));
        }

        Assert.Empty(tracker.Snapshot(T0.AddSeconds(7)));
    }

    [Fact]
    public void VisibleReading_ReconcilesExtrapolation()
    {
        var tracker = new Tracker();
        tracker.Observe(12, [Row(0, "滋水枪", ArtifactState.Cooling, 30)], T0);
        tracker.Observe(1, [Row(0, "别的神器")], T0.AddSeconds(1));

        TrackerEntryView before = tracker.Snapshot(T0.AddSeconds(4)).Single(e => e.Page == VisiblePage.Page1);
        Assert.Equal(26, before.CooldownSeconds);

        tracker.Observe(12, [Row(0, "滋水枪", ArtifactState.Cooling, 25)], T0.AddSeconds(5));
        TrackerEntryView after = tracker.Snapshot(T0.AddSeconds(5)).Single(e => e.Page == VisiblePage.Page1);

        Assert.Equal(25, after.CooldownSeconds);
        Assert.Equal(EntrySource.Live, after.Source);
    }

    [Fact]
    public void UsesToken_IsTrackedPerSlot()
    {
        var tracker = new Tracker();

        tracker.Observe(12, [new ObservedRow(2, "荧光棒", ArtifactState.Ready, null, 4, 5)], T0);

        TrackerEntryView view = tracker.Snapshot(T0)[0];
        Assert.Equal(4, view.UsesRemaining);
        Assert.Equal(5, view.UsesTotal);
    }

    [Fact]
    public void MissedUsesToken_KeepsPreviousValueInsteadOfFlickering()
    {
        // 实测：OCR 会间歇性漏读 `n/m`（`[R]4/5` 有时只读到 `[R]`），整行覆盖会让 uses 闪烁
        var tracker = new Tracker();
        tracker.Observe(12, [new ObservedRow(0, "荧光棒", ArtifactState.Ready, null, 4, 5)], T0);

        tracker.Observe(12, [Row(0, "荧光棒")], T0.AddSeconds(1));

        TrackerEntryView view = tracker.Snapshot(T0.AddSeconds(1))[0];
        Assert.Equal(4, view.UsesRemaining);
        Assert.Equal(5, view.UsesTotal);
    }

    [Fact]
    public void UsesTokenChanged_IsUpdated()
    {
        var tracker = new Tracker();
        tracker.Observe(12, [new ObservedRow(0, "荧光棒", ArtifactState.Ready, null, 4, 5)], T0);

        tracker.Observe(12, [new ObservedRow(0, "荧光棒", ArtifactState.Ready, null, 3, 5)], T0.AddSeconds(2));

        Assert.Equal(3, tracker.Snapshot(T0.AddSeconds(2))[0].UsesRemaining);
    }

    [Fact]
    public void SlotSwitchingArtifact_ClearsStaleUses()
    {
        var tracker = new Tracker();
        tracker.Observe(12, [new ObservedRow(0, "荧光棒", ArtifactState.Ready, null, 4, 5)], T0);

        // 同一槽位换了神器且没读 n/m ⇒ 不能把上一条的 uses 留在新神器上
        tracker.Observe(12, [Row(0, "滋水枪")], T0.AddSeconds(2));

        TrackerEntryView view = tracker.Snapshot(T0.AddSeconds(2))[0];
        Assert.Equal("滋水枪", view.ArtifactName);
        Assert.Null(view.UsesRemaining);
        Assert.Null(view.UsesTotal);
    }

    [Fact]
    public void MissingOnVisiblePage_DisappearsAfterSixSeconds()
    {
        var tracker = new Tracker();
        tracker.Observe(12, [Row(0, "滋水枪"), Row(1, "荧光棒")], T0);

        // 5 秒：仍在
        tracker.Observe(12, [Row(0, "滋水枪")], T0.AddSeconds(5));
        Assert.Contains(tracker.Snapshot(T0.AddSeconds(5)), e => e.Slot == 1);

        // 7 秒：第 1 行超过 6 秒没扫到 ⇒ 自动消失
        TrackerFrameResult result = tracker.Observe(12, [Row(0, "滋水枪")], T0.AddSeconds(7));

        Assert.Contains("荧光棒", result.Removed[0]);
        Assert.DoesNotContain(tracker.Snapshot(T0.AddSeconds(7)), e => e.Slot == 1);
    }

    [Fact]
    public void MissingOnVisiblePage_NotRemovedExactlyAtTimeout()
    {
        var tracker = new Tracker();
        tracker.Observe(12, [Row(0, "滋水枪")], T0);

        // 恰好 6 秒：还没超过时限，不应移除
        TrackerFrameResult result = tracker.Observe(12, [], T0.AddSeconds(6));

        Assert.Empty(result.Removed);
        Assert.Single(tracker.Snapshot(T0.AddSeconds(6)));
    }

    [Fact]
    public void DisappearAfterSeconds_IsConfigurable()
    {
        var tracker = new Tracker(new TrackerOptions { DisappearAfterSeconds = 2 });
        tracker.Observe(12, [Row(0, "滋水枪")], T0);

        Assert.Empty(tracker.Observe(12, [], T0.AddSeconds(1)).Removed);
        TrackerFrameResult result = tracker.Observe(12, [], T0.AddSeconds(3));

        Assert.Contains("滋水枪", result.Removed[0]);
    }

    [Fact]
    public void PlayerName_IsTrackedAndUpdated()
    {
        var tracker = new Tracker();
        tracker.Observe(12, [new ObservedRow(0, "滋水枪", ArtifactState.Ready, null, null, null, "亦陌雕")], T0);
        Assert.Equal("亦陌雕", tracker.Snapshot(T0)[0].PlayerName);

        tracker.Observe(12, [new ObservedRow(0, "滋水枪", ArtifactState.Ready, null, null, null, "楚门潇")], T0.AddSeconds(1));
        Assert.Equal("楚门潇", tracker.Snapshot(T0.AddSeconds(1))[0].PlayerName);
    }

    private static ObservedRow Row(
        int slot,
        string artifact,
        ArtifactState state = ArtifactState.Ready,
        int? cooldown = null,
        int? usesRemaining = null,
        int? usesTotal = null)
        => new(slot, artifact, state, cooldown, usesRemaining, usesTotal);
}
