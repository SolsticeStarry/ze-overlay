using ZeOverlay.Shared;

namespace ZeOverlay.Tests.Scenarios;

/// <summary>
/// FYS 社区服（entWatch 连写格式：`简称+标号 玩家名 状态`，状态为 `就绪`/`NNs`/`∞`）
/// 的端到端场景回放。该服列表在神器被捡起/用掉后会**回流**（下方整体上移），
/// 跟踪器按「名称 + 服务器标号」身份识别，因此换行不会产生重复条目。
/// </summary>
public sealed class FysServerScenarioTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static string Ready(string name, int index, string player) => $"{name}{index}{player}就绪";

    private static string Cooling(string name, int index, string player, int seconds) => $"{name}{index}{player}{seconds}s";

    [Fact]
    public void PickUp_NewArtifact_IsAddedExactlyOnce()
    {
        var sim = new ServerSimulator(ServerKind.Fys, T0);
        sim.Feed(0, Ready("手电", 1, "熊"), Ready("定位", 1, "小明"));

        // 玩家捡起「水枪1」：列表多出一行。
        TrackerFrameResult result = sim.Feed(
            1,
            Ready("手电", 1, "熊"),
            Ready("定位", 1, "小明"),
            Ready("水枪", 1, "老王"));

        Assert.Single(result.Added);
        Assert.Empty(result.Removed);

        IReadOnlyList<TrackerEntryView> snapshot = sim.Snapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Contains(snapshot, e => e.ArtifactName == "水枪" && e.ServerIndex == 1);
    }

    [Fact]
    public void PickUp_InsertedAtTop_ReflowKeepsExistingIdentity()
    {
        // 「爆闪1」被捡起插在顶部，下面三行整体上移（回流）。
        // 标号身份下，回流只是刷新，不是——也不能是——新条目。
        var sim = new ServerSimulator(ServerKind.Fys, T0);
        sim.Feed(0, Ready("手电", 1, "熊"), Cooling("定位", 1, "小明", 12), Ready("水枪", 1, "老王"));

        TrackerFrameResult result = sim.Feed(
            1,
            Ready("爆闪", 1, "绿光"),      // 新捡起，插在顶部
            Ready("手电", 1, "熊"),
            Cooling("定位", 1, "小明", 11),
            Ready("水枪", 1, "老王"));

        Assert.Single(result.Added);                          // 只有爆闪是新增
        Assert.Empty(result.Removed);

        IReadOnlyList<TrackerEntryView> snapshot = sim.Snapshot();
        Assert.Equal(4, snapshot.Count);
        Assert.Contains(snapshot, e => e.ArtifactName == "爆闪" && e.ServerIndex == 1);
        Assert.Equal(
            snapshot.Count,
            snapshot.Select(e => (e.ArtifactName, e.ServerIndex)).Distinct().Count());

        // 老三行回流后仍是同一条，且倒计时按画面重校。
        Assert.Contains(snapshot, e => e.ArtifactName == "手电" && e.ServerIndex == 1);
        Assert.Contains(snapshot, e => e.ArtifactName == "定位" && e.ServerIndex == 1 && e.CooldownSeconds == 11);
        Assert.Contains(snapshot, e => e.ArtifactName == "水枪" && e.ServerIndex == 1);
    }

    [Fact]
    public void ArtifactUsedUp_ReflowKeepsOtherRows()
    {
        var sim = new ServerSimulator(ServerKind.Fys, T0);
        sim.Feed(0, Ready("手电", 1, "熊"), Cooling("定位", 1, "小明", 12), Ready("水枪", 1, "老王"));

        // 「定位1」被用掉，水枪整体上移；定位未超时前仍外推保留。
        TrackerFrameResult result = sim.Feed(1, Ready("手电", 1, "熊"), Ready("水枪", 1, "老王"));

        Assert.Empty(result.Added);
        Assert.Empty(result.Removed);

        IReadOnlyList<TrackerEntryView> snapshot = sim.Snapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Contains(snapshot, e => e.ArtifactName == "水枪" && e.ServerIndex == 1);
        Assert.Contains(snapshot, e => e.ArtifactName == "定位" && e.Source == EntrySource.Extrapolated);
    }

    [Fact]
    public void ArtifactLost_DisappearsAfterTimeout()
    {
        var sim = new ServerSimulator(ServerKind.Fys, T0);
        sim.Feed(0, Ready("手电", 1, "熊"), Cooling("定位", 1, "小明", 12), Ready("水枪", 1, "老王"));

        // 定位1 消失；未超时仍保留。
        sim.Feed(2, Ready("手电", 1, "熊"), Ready("水枪", 1, "老王"));
        Assert.Contains(sim.Snapshot(), e => e.ArtifactName == "定位");

        // 超过 6 秒无条件超时 ⇒ 移除，其余回流条目不受影响。
        TrackerFrameResult result = sim.Feed(8, Ready("手电", 1, "熊"), Ready("水枪", 1, "老王"));

        Assert.Contains("定位", result.Removed[0]);
        Assert.DoesNotContain(sim.Snapshot(), e => e.ArtifactName == "定位");
        Assert.Equal(2, sim.Snapshot().Count);
    }

    [Fact]
    public void NormalUse_CountdownExtrapolatesThenReconcilesOnRead()
    {
        var sim = new ServerSimulator(ServerKind.Fys, T0);
        sim.Feed(0, Cooling("水枪", 1, "老王", 30));

        Assert.Equal(30, Single(sim.SnapshotAt(0), "水枪").CooldownSeconds);

        TrackerEntryView extrapolated = Single(sim.SnapshotAt(5), "水枪");
        Assert.Equal(25, extrapolated.CooldownSeconds);

        sim.Feed(5, Cooling("水枪", 1, "老王", 24));
        TrackerEntryView reconciled = Single(sim.Snapshot(), "水枪");
        Assert.Equal(24, reconciled.CooldownSeconds);
        Assert.Equal(EntrySource.Live, reconciled.Source);

        TrackerEntryView done = Single(sim.SnapshotAt(30), "水枪");
        Assert.Equal(0, done.CooldownSeconds);
        Assert.Equal(ArtifactState.Ready, done.State);
    }

    [Fact]
    public void SmallDecimalCooldown_IsTruncatedToInteger()
    {
        // 真机 FYS 样本里出现过 `1.5s`；小数被丢弃只取整数（见 DESIGN §3.8）。
        var sim = new ServerSimulator(ServerKind.Fys, T0);

        sim.Feed(0, "定位2雨露之夏1.5s");

        Assert.Equal(1, Single(sim.Snapshot(), "定位").CooldownSeconds);
    }

    private static TrackerEntryView Single(IReadOnlyList<TrackerEntryView> entries, string name)
        => Assert.Single(entries.Where(e => e.ArtifactName == name));
}
