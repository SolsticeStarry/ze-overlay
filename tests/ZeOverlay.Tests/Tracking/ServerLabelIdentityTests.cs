using ZeOverlay.Shared;
using ZeOverlay.Stage.Tracking;

namespace ZeOverlay.Tests.Tracking;

/// <summary>
/// 社区服列表在神器被用掉后会**回流**（下方整体上移），槽位不再稳定。
/// 开启 <see cref="TrackerOptions.KeyByServerLabel"/> 后身份改用「名称 + 服务器标号」，
/// 回流不会产生重复条目。
/// </summary>
public sealed class ServerLabelIdentityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 0, 10, 0, TimeSpan.Zero);

    private static ObservedRow Row(int slot, string name, int serverIndex)
        => new(slot, name, ArtifactState.Ready, null, null, null, string.Empty, serverIndex);

    [Fact]
    public void Reflow_KeepsIdentity_WhenKeyedByServerLabel()
    {
        var tracker = new Tracker(new TrackerOptions { KeyByServerLabel = true });

        // 第一帧：定位3 在槽位 5，黑闪1 在槽位 6。
        tracker.Observe(12, [Row(5, "定位", 3), Row(6, "黑闪", 1)], T0);

        // 第二帧：定位3 被用掉，列表回流，黑闪1 上移到槽位 5。
        TrackerFrameResult result = tracker.Observe(12, [Row(5, "黑闪", 1)], T0.AddSeconds(1));

        Assert.Empty(result.Added); // 只是刷新，不是新条目
        Assert.Contains(
            tracker.Snapshot(T0.AddSeconds(1)),
            e => e.ArtifactName == "黑闪" && e.ServerIndex == 1);
    }

    [Fact]
    public void SameName_DifferentServerIndex_AreSeparateEntries()
    {
        var tracker = new Tracker(new TrackerOptions { KeyByServerLabel = true });

        tracker.Observe(12, [Row(0, "手电", 1), Row(1, "手电", 2), Row(2, "手电", 3)], T0);

        Assert.Equal(3, tracker.Snapshot(T0).Count);
    }

    [Fact]
    public void MissingIndex_IsSkippedInsteadOfGuessingLabel()
    {
        var tracker = new Tracker();

        tracker.Observe(12, [new ObservedRow(3, "手电", ArtifactState.Ready, null, null, null, string.Empty, 2)], T0);

        // 下一帧「手电」标号整段读丢：**不猜标号**（猜会污染同名兄弟），该行跳过；
        // 原有的 手电2 保留（转为外推），同帧的 定位1 正常新建。
        tracker.Observe(
            12,
            [
                new ObservedRow(3, "手电", ArtifactState.Ready),
                new ObservedRow(5, "定位", ArtifactState.Ready, null, null, null, string.Empty, 1),
            ],
            T0.AddSeconds(1));

        IReadOnlyList<TrackerEntryView> snapshot = tracker.Snapshot(T0.AddSeconds(1));
        TrackerEntryView hand = Assert.Single(snapshot.Where(e => e.ArtifactName == "手电"));
        Assert.Equal(2, hand.ServerIndex);
        Assert.Contains(snapshot, e => e.ArtifactName == "定位" && e.ServerIndex == 1);
    }

    [Fact]
    public void IndexMisread_AtSameSlot_DoesNotMergeSibling_AndSelfHeals()
    {
        var tracker = new Tracker();

        tracker.Observe(12, [new ObservedRow(4, "定位", ArtifactState.Ready, null, null, null, string.Empty, 3)], T0);

        // 同一行位把标号 3 读成 9：不再「借标号」硬并（同类多神器下那会把真实兄弟并掉、丢条目），
        // 而是当作一个新标号；原来的 定位3 保留。
        tracker.Observe(
            12,
            [new ObservedRow(4, "定位", ArtifactState.Ready, null, null, null, string.Empty, 9)],
            T0.AddSeconds(1));

        IReadOnlyList<TrackerEntryView> afterMisread = tracker.Snapshot(T0.AddSeconds(1));
        Assert.Contains(afterMisread, e => e.ServerIndex == 3);
        Assert.Contains(afterMisread, e => e.ServerIndex == 9);

        // 读花条目没有后续更新 ⇒ 超过消失超时后自动消失，不会长期污染。
        tracker.Observe(12, [new ObservedRow(4, "定位", ArtifactState.Ready, null, null, null, string.Empty, 3)], T0.AddSeconds(8));
        Assert.DoesNotContain(tracker.Snapshot(T0.AddSeconds(8)), e => e.ServerIndex == 9);
    }

    [Fact]
    public void TrackingStage_PassesServerIndexThrough()
    {
        var tracker = new Tracker(new TrackerOptions { KeyByServerLabel = true });
        var stage = new TrackingStage(tracker);

        stage.Process(new TrackingInput(
            12,
            [new ParsedRow("手电", "熊", ArtifactState.Ready, null, null, null, "手电1熊就绪", 5, 3)],
            T0));

        Assert.Equal(3, Assert.Single(tracker.Snapshot(T0)).ServerIndex);
    }

    [Fact]
    public void SlotIdentity_StillUsedByDefault()
    {
        var tracker = new Tracker();

        tracker.Observe(12, [new ObservedRow(5, "滋水枪", ArtifactState.Ready)], T0);
        tracker.Observe(12, [new ObservedRow(5, "荧光棒", ArtifactState.Ready)], T0.AddSeconds(1));

        // 默认按槽位：同一行换内容不算新条目（本服行为不变）。
        Assert.Single(tracker.Snapshot(T0.AddSeconds(1)));
    }

    [Fact]
    public void SameNameMultipleIndices_Reflow_KeepsEachSibling()
    {
        var tracker = new Tracker(new TrackerOptions { KeyByServerLabel = true });

        // 水枪1 / 水枪2 在场。
        tracker.Observe(12, [Row(0, "水枪", 1), Row(1, "水枪", 2)], T0);

        // 回流：水枪3 插到顶部，水枪1 落到原来水枪2 的行位（同行位、同名，但标号不同）。
        tracker.Observe(12, [Row(0, "水枪", 3), Row(1, "水枪", 1), Row(2, "水枪", 2)], T0.AddSeconds(1));

        IReadOnlyList<TrackerEntryView> snapshot = tracker.Snapshot(T0.AddSeconds(1));

        Assert.Equal(3, snapshot.Count);
        Assert.Contains(snapshot, e => e.ArtifactName == "水枪" && e.ServerIndex == 1);
        Assert.Contains(snapshot, e => e.ArtifactName == "水枪" && e.ServerIndex == 2);
        Assert.Contains(snapshot, e => e.ArtifactName == "水枪" && e.ServerIndex == 3);
    }

    [Fact]
    public void SameNameSibling_ReadAtOldSlot_IsNotMergedIntoOldLabel()
    {
        var tracker = new Tracker(new TrackerOptions { KeyByServerLabel = true });

        // 先只有水枪2。随后同一行位读到真正的兄弟 水枪1（本帧还有 水枪3 ⇒ 判定为同类多神器）。
        tracker.Observe(12, [Row(0, "水枪", 2)], T0);
        tracker.Observe(12, [Row(0, "水枪", 1), Row(1, "水枪", 3)], T0.AddSeconds(1));

        IReadOnlyList<TrackerEntryView> snapshot = tracker.Snapshot(T0.AddSeconds(1));

        Assert.Contains(snapshot, e => e.ServerIndex == 1);
        Assert.Contains(snapshot, e => e.ServerIndex == 3);
        Assert.Contains(snapshot, e => e.ServerIndex == 2); // 未超时，仍在外推
    }

    [Fact]
    public void MissingIndex_WithSameNameFamily_IsSkippedInsteadOfStealingLabel()
    {
        var tracker = new Tracker();

        // 同类多神器：水枪1 / 水枪2 同时在场（不同冷却）。
        tracker.Observe(
            12,
            [
                new ObservedRow(0, "水枪", ArtifactState.Cooling, 12, null, null, string.Empty, 1),
                new ObservedRow(1, "水枪", ArtifactState.Cooling, 7, null, null, string.Empty, 2),
            ],
            T0);

        // 下一帧：水枪2 的标号被整段读丢（null）。同名已有多把 ⇒ 不能借标号（否则会改写水枪1）。
        tracker.Observe(
            12,
            [
                new ObservedRow(0, "水枪", ArtifactState.Cooling, 12, null, null, string.Empty, 1),
                new ObservedRow(1, "水枪", ArtifactState.Cooling, 7, null, null, string.Empty, null),
            ],
            T0.AddSeconds(1));

        IReadOnlyList<TrackerEntryView> snapshot = tracker.Snapshot(T0.AddSeconds(1));

        Assert.Equal(2, snapshot.Count);
        Assert.Equal(12, snapshot.Single(e => e.ServerIndex == 1).CooldownSeconds);
        Assert.Equal(6, snapshot.Single(e => e.ServerIndex == 2).CooldownSeconds); // 未被借走改写，仍按外推
    }
}
