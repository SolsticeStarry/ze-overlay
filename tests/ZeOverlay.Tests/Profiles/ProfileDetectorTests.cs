using ZeOverlay.Shared;

namespace ZeOverlay.Tests.Profiles;

/// <summary>自动选档的纯逻辑：按名字命中数选档案，带最小命中与领先余量，避免抖动。</summary>
public sealed class ProfileDetectorTests
{
    [Fact]
    public void ActiveIsBest_DoesNotSwitch()
        => Assert.Null(ProfileDetector.Choose(
            "bracket",
            [new ProfileScore("bracket", 5), new ProfileScore("community", 3)]));

    [Fact]
    public void OtherClearlyBetter_Switches()
        => Assert.Equal(
            "community",
            ProfileDetector.Choose(
                "bracket",
                [new ProfileScore("bracket", 1), new ProfileScore("community", 5)]));

    [Fact]
    public void Tie_DoesNotSwitch()
        => Assert.Null(ProfileDetector.Choose(
            "bracket",
            [new ProfileScore("bracket", 5), new ProfileScore("community", 5)]));

    [Fact]
    public void OneMoreHit_IsEnough()
        => Assert.Equal(
            "community",
            ProfileDetector.Choose(
                "bracket",
                [new ProfileScore("bracket", 4), new ProfileScore("community", 5)]));

    [Fact]
    public void BelowMinHits_DoesNotSwitch()
        => Assert.Null(ProfileDetector.Choose(
            "bracket",
            [new ProfileScore("bracket", 0), new ProfileScore("community", 1)]));

    [Fact]
    public void ActiveMissingFromScores_TreatedAsZero()
        => Assert.Equal(
            "community",
            ProfileDetector.Choose("bracket", [new ProfileScore("community", 3)]));

    [Fact]
    public void NoScores_DoesNotSwitch()
        => Assert.Null(ProfileDetector.Choose("bracket", []));
}
