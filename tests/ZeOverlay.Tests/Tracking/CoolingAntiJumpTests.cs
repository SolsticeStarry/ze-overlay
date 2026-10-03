using ZeOverlay.Shared;
using ZeOverlay.Stage.Tracking;

namespace ZeOverlay.Tests.Tracking;

/// <summary>
/// 冷却只会下降。社区服 HUD 有小数冷却（`1.5s`），OCR 容易把小数点读丢变成整数
/// （`3.5s`→`35s`），若直接采纳会让倒计时乱跳。同一神器上远大于外推值的读数应被忽略。
/// </summary>
public sealed class CoolingAntiJumpTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 0, 20, 0, TimeSpan.Zero);

    private static ObservedRow Cooling(string name, int seconds)
        => new(0, name, ArtifactState.Cooling, seconds);

    [Fact]
    public void ImplausibleJumpUp_IsIgnored()
    {
        var tracker = new Tracker();
        tracker.Observe(12, [Cooling("滋水枪", 4)], T0);

        // 1 秒后外推约 3；读数尖峰 40（`4.0s` 丢点读成 `40s`）应被忽略。
        tracker.Observe(12, [Cooling("滋水枪", 40)], T0.AddSeconds(1));

        Assert.Equal(3, tracker.Snapshot(T0.AddSeconds(1)).Single().CooldownSeconds);
    }

    [Fact]
    public void PlausibleDecrease_IsAccepted()
    {
        var tracker = new Tracker();
        tracker.Observe(12, [Cooling("滋水枪", 30)], T0);
        tracker.Observe(12, [Cooling("滋水枪", 28)], T0.AddSeconds(1));

        Assert.Equal(28, tracker.Snapshot(T0.AddSeconds(1)).Single().CooldownSeconds);
    }

    [Fact]
    public void ResetAfterReady_IsAccepted()
    {
        var tracker = new Tracker();
        tracker.Observe(12, [Cooling("滋水枪", 3)], T0);
        tracker.Observe(12, [new ObservedRow(0, "滋水枪", ArtifactState.Ready)], T0.AddSeconds(1));

        // 先就绪，再重新进入长冷却 ⇒ 这是合法重置，必须接受。
        tracker.Observe(12, [Cooling("滋水枪", 60)], T0.AddSeconds(2));

        Assert.Equal(60, tracker.Snapshot(T0.AddSeconds(2)).Single().CooldownSeconds);
    }

    [Fact]
    public void ReUse_WhenCooldownNearlyDone_IsAccepted()
    {
        var tracker = new Tracker();
        tracker.Observe(12, [Cooling("滋水枪", 2)], T0);

        // 下一秒读数变成 30：外推已 = 1（冷却基本走完），这是**再次使用**给的合法新冷却。
        // 1Hz 采样可能看不到中间的 `[R]` 帧，不能被当成"读花尖峰"拒收，否则会一直卡在就绪。
        tracker.Observe(12, [Cooling("滋水枪", 30)], T0.AddSeconds(1));

        Assert.Equal(30, tracker.Snapshot(T0.AddSeconds(1)).Single().CooldownSeconds);
    }

    [Fact]
    public void ImplausibleDrop_DroppedTensDigit_IsIgnored()
    {
        var tracker = new Tracker();
        tracker.Observe(12, [Cooling("滋水枪", 15)], T0);

        // 下一秒读数变成 5：`[15]` 的十位被 OCR 读丢（`[1` 被读成 `门`）⇒ 一次掉 10s 不可能，
        // 应沿用外推（≈14），不能把两位数显示成个位数。
        tracker.Observe(12, [Cooling("滋水枪", 5)], T0.AddSeconds(1));

        int? shown = tracker.Snapshot(T0.AddSeconds(1)).Single().CooldownSeconds;
        Assert.InRange(shown ?? -1, 13, 14);
    }

    [Fact]
    public void PlausibleGradualDecrease_IsAccepted()
    {
        var tracker = new Tracker();
        tracker.Observe(12, [Cooling("滋水枪", 15)], T0);
        tracker.Observe(12, [Cooling("滋水枪", 14)], T0.AddSeconds(1));

        Assert.Equal(14, tracker.Snapshot(T0.AddSeconds(1)).Single().CooldownSeconds);
    }

    [Fact]
    public void CooldownVote_SmoothsWithinToleranceLowRead()
    {
        var tracker = new Tracker();
        tracker.Observe(12, [Cooling("滋水枪", 20)], T0);
        tracker.Observe(12, [Cooling("滋水枪", 19)], T0.AddSeconds(1));

        // 第三帧读到 16：在防跳变容差内（外推 18，容许 16..19）⇒ 会进历史；
        // 但多帧投票把 16 判为单帧读花：三次读数外推到当前 = 18/18/16 ⇒ 中位 18。
        tracker.Observe(12, [Cooling("滋水枪", 16)], T0.AddSeconds(2));

        Assert.Equal(18, tracker.Snapshot(T0.AddSeconds(2)).Single().CooldownSeconds);
    }

    [Fact]
    public void CooldownVote_OutvotedBlip_KeepsTrend()
    {
        var tracker = new Tracker();
        tracker.Observe(12, [Cooling("滋水枪", 30)], T0);
        tracker.Observe(12, [Cooling("滋水枪", 29)], T0.AddSeconds(1));
        tracker.Observe(12, [Cooling("滋水枪", 28)], T0.AddSeconds(2));

        // 第 4 帧低读 25（外推 27，容差内）⇒ 三次窗口为 29/28/25 → 中位 27，而不是 25。
        tracker.Observe(12, [Cooling("滋水枪", 25)], T0.AddSeconds(3));

        Assert.Equal(27, tracker.Snapshot(T0.AddSeconds(3)).Single().CooldownSeconds);
    }

    [Fact]
    public void CooldownVote_ClearedOnReset()
    {
        var tracker = new Tracker();
        tracker.Observe(12, [Cooling("滋水枪", 60)], T0);
        tracker.Observe(12, [Cooling("滋水枪", 59)], T0.AddSeconds(1));

        // 就绪 ⇒ 清空投票历史；随后再次进入冷却应直接采信新值，不被旧的长冷却拖住。
        tracker.Observe(12, [new ObservedRow(0, "滋水枪", ArtifactState.Ready)], T0.AddSeconds(2));
        tracker.Observe(12, [Cooling("滋水枪", 30)], T0.AddSeconds(3));

        Assert.Equal(30, tracker.Snapshot(T0.AddSeconds(3)).Single().CooldownSeconds);
    }

    [Fact]
    public void LeadingDigitMisread_IsNotAdopted()
    {
        var tracker = new Tracker();

        // `[49]` 前面无端多出一个 8 ⇒ 849：量级不合理（>300），不采纳（新条目还没有可信值）。
        tracker.Observe(12, [Cooling("滋水枪", 849)], T0);
        Assert.Null(tracker.Snapshot(T0).Single().CooldownSeconds);

        // 下一帧读回真值 49 ⇒ 正常采纳。
        tracker.Observe(12, [Cooling("滋水枪", 49)], T0.AddSeconds(1));
        Assert.Equal(49, tracker.Snapshot(T0.AddSeconds(1)).Single().CooldownSeconds);
    }

    [Fact]
    public void StuckWrongValue_RecoversAfterTwoConsistentReads()
    {
        var tracker = new Tracker();

        // 量级在界内但仍是读花的错值（249）会被采纳——此时靠"连续一致"纠正。
        tracker.Observe(12, [Cooling("滋水枪", 249)], T0);
        Assert.Equal(249, tracker.Snapshot(T0).Single().CooldownSeconds);

        // 之后连续两帧读回真值 48/47：第一次只作候选，第二次一致 ⇒ 纠正（否则会卡在 249 十几分钟）。
        tracker.Observe(12, [Cooling("滋水枪", 48)], T0.AddSeconds(1));
        tracker.Observe(12, [Cooling("滋水枪", 47)], T0.AddSeconds(2));

        Assert.Equal(47, tracker.Snapshot(T0.AddSeconds(2)).Single().CooldownSeconds);
    }
}
