using ZeOverlay.Shared;

namespace ZeOverlay.Tests.Scenarios;

/// <summary>
/// EXG 社区服（括号格式：`名称 [R]/[数字] 可选n/m 玩家名`）的端到端场景回放。
/// 该服身份 = `(页, 行槽位)`，所以「换行」表现为各槽位内容整体上/下移
/// （列表顶部锚定、行距固定）。逐行自动分流保证这些行不会被 FYS 的连写语法误吃。
/// </summary>
public sealed class ExgServerScenarioTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static string Ready(string name, string player) => $"{name} [R] {player}";

    private static string Cooling(string name, int seconds, string player) => $"{name} [{seconds}] {player}";

    [Fact]
    public void PickUp_NewArtifactAppended_IsAddedExactlyOnce()
    {
        var sim = new ServerSimulator(ServerKind.Exg, T0);
        sim.Feed(0, Ready("皇家口粮", "甲"), Ready("滋水枪", "乙"));

        // 玩家捡起第 3 个神器：追加到列表底部（新槽位）。
        TrackerFrameResult result = sim.Feed(
            1,
            Ready("皇家口粮", "甲"),
            Ready("滋水枪", "乙"),
            Ready("荧光棒", "丙"));

        Assert.Single(result.Added);
        Assert.Empty(result.Removed);

        // 注：TrackerFrameResult.Added 的显示名在「新建条目当帧」为空
        // （Tracker 先记事件、后写字段），因此用快照断言名称。
        IReadOnlyList<TrackerEntryView> snapshot = sim.Snapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Contains(snapshot, e => e.ArtifactName == "荧光棒");
    }

    [Fact]
    public void PickUp_InsertedAtTop_ShiftsRows_NoDuplicateEntries()
    {
        // 神器被捡起时插在列表中间 ⇒ 下方整体下移（「神器换行」）。
        // EXG 按行槽位身份，表现为各槽位内容被重映射；关键是**不产生重复条目、数量正确**。
        var sim = new ServerSimulator(ServerKind.Exg, T0);
        sim.Feed(0, Ready("皇家口粮", "甲"), Ready("滋水枪", "乙"), Ready("手电筒", "丙"));

        sim.Feed(
            1,
            Ready("荧光棒", "丁"),
            Ready("皇家口粮", "甲"),
            Ready("滋水枪", "乙"),
            Ready("手电筒", "丙"));

        IReadOnlyList<string> names = sim.Snapshot().Select(e => e.ArtifactName).ToList();

        Assert.Equal(4, names.Count);
        Assert.Equal(names.Count, names.Distinct().Count()); // 无重复条目
        Assert.Contains("荧光棒", names);
        Assert.Contains("皇家口粮", names);
    }

    [Fact]
    public void ArtifactLost_DisappearsAfterTimeout()
    {
        var sim = new ServerSimulator(ServerKind.Exg, T0);
        sim.Feed(0, Ready("皇家口粮", "甲"), Ready("滋水枪", "乙"), Ready("荧光棒", "丙"));

        // 荧光棒从底部消失，行数 3→2 仍是第 1 页；未超时就先保留（外推）。
        sim.Feed(2, Ready("皇家口粮", "甲"), Ready("滋水枪", "乙"));
        Assert.Contains(sim.Snapshot(), e => e.ArtifactName == "荧光棒");

        // 超过 6 秒无条件超时 ⇒ 自动移除。
        TrackerFrameResult result = sim.Feed(8, Ready("皇家口粮", "甲"), Ready("滋水枪", "乙"));

        Assert.Contains("荧光棒", result.Removed[0]);
        Assert.DoesNotContain(sim.Snapshot(), e => e.ArtifactName == "荧光棒");
        Assert.Equal(2, sim.Snapshot().Count);
    }

    [Fact]
    public void NormalUse_CountdownExtrapolatesThenReconcilesOnRead()
    {
        var sim = new ServerSimulator(ServerKind.Exg, T0);
        sim.Feed(0, Cooling("滋水枪", 30, "甲"));

        Assert.Equal(30, Single(sim.SnapshotAt(0), "滋水枪").CooldownSeconds);

        // 画面没重读的这 5 秒：按墙钟本地外推（Source 只在 Observe 时变化，这里不查）。
        TrackerEntryView extrapolated = Single(sim.SnapshotAt(5), "滋水枪");
        Assert.Equal(25, extrapolated.CooldownSeconds);

        // 再次被看到：用画面读数重校。
        sim.Feed(5, Cooling("滋水枪", 24, "甲"));
        TrackerEntryView reconciled = Single(sim.Snapshot(), "滋水枪");
        Assert.Equal(24, reconciled.CooldownSeconds);
        Assert.Equal(EntrySource.Live, reconciled.Source);

        // 倒计时归零 ⇒ 显示为就绪。
        TrackerEntryView done = Single(sim.SnapshotAt(30), "滋水枪");
        Assert.Equal(0, done.CooldownSeconds);
        Assert.Equal(ArtifactState.Ready, done.State);
    }

    [Fact]
    public void Display_ShowsAllEntries_WhenWatchlistEmpty()
    {
        // S4→S6 全链路：名单为空时匹配器不过滤，展示条目与跟踪条目一致。
        var sim = new ServerSimulator(ServerKind.Exg, T0);
        sim.Feed(0, Ready("皇家口粮", "甲"), Cooling("滋水枪", 20, "乙"));

        Assert.Equal(2, sim.LastDisplay.Count);
        Assert.Contains(
            sim.LastDisplay,
            d => d.Name == "滋水枪" && d.State == ArtifactState.Cooling && d.CooldownSeconds == 20);
    }

    private static TrackerEntryView Single(IReadOnlyList<TrackerEntryView> entries, string name)
        => Assert.Single(entries.Where(e => e.ArtifactName == name));
}
