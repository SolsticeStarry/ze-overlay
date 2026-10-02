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
}
