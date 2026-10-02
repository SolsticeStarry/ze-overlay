using ZeOverlay.Core.Matching;

namespace ZeOverlay.Core.Tests.Matching;

/// <summary>
/// 关注名单匹配：既是过滤器也是纠错器。测试用例来自真实识别结果。
/// </summary>
public class WatchlistMatcherTests
{
    private static readonly string[] SampleWatchlist = ["滋水枪", "荧光棒", "紫色瓶中闪电", "皇家口粮"];

    [Fact]
    public void ExactMatch_ReturnsCanonicalName()
    {
        var matcher = new WatchlistMatcher(SampleWatchlist);

        WatchlistMatch? match = matcher.Match("滋水枪");

        Assert.NotNull(match);
        Assert.Equal("滋水枪", match.CanonicalName);
        Assert.Equal(1.0, match.Score, 3);
    }

    [Fact]
    public void NameOutsideWatchlist_DoesNotMatch()
    {
        var matcher = new WatchlistMatcher(SampleWatchlist);

        Assert.Null(matcher.Match("袋装火盐"));
        Assert.Null(matcher.Match("手电筒"));
        Assert.Null(matcher.Match("桶装杏仁水"));
    }

    [Fact]
    public void SimilarButDifferentArtifact_IsNotMisMatched()
    {
        // 实测误报：阈值 0.6~0.8 时「黑色瓶中闪电」会命中名单里的「紫色瓶中闪电」（只差首字 ≈ 0.83）。
        // 默认阈值改成 0.8 后必须挡住。
        var matcher = new WatchlistMatcher(SampleWatchlist);

        Assert.Null(matcher.Match("黑色瓶中闪电"));
    }

    [Fact]
    public void OcrErrorInLongerName_IsStillCorrected()
    {
        // 末字被读错（电→屯）：6 字名字错 1 字 = 0.833
        var strict = new WatchlistMatcher(SampleWatchlist);
        Assert.Null(strict.Match("紫色瓶中闪屯"));

        // 放宽阈值即可纠正（宽松度可调）
        var loose = new WatchlistMatcher(SampleWatchlist, new WatchlistMatcherOptions { Threshold = 0.7 });
        WatchlistMatch? match = loose.Match("紫色瓶中闪屯");
        Assert.NotNull(match);
        Assert.Equal("紫色瓶中闪电", match.CanonicalName);
    }

    [Fact]
    public void SimilarEvenAt01Threshold()
    {
        var matcher = new WatchlistMatcher(SampleWatchlist);

        // 完全无关的名字在很松的阈值下也不该命中
        Assert.Null(matcher.Match("完全无关的东西"));
    }

    [Fact]
    public void EmptyWatchlist_MatchesNothing()
    {
        var matcher = new WatchlistMatcher(null);

        Assert.True(matcher.IsEmpty);
        Assert.Null(matcher.Match("滋水枪"));
    }

    [Fact]
    public void WhitespaceAndFullWidth_AreNormalized()
    {
        var matcher = new WatchlistMatcher(SampleWatchlist);

        Assert.NotNull(matcher.Match(" 滋 水 枪 "));
    }

    [Fact]
    public void DuplicateEntries_AreDeduplicated()
    {
        var matcher = new WatchlistMatcher(["滋水枪", "滋水枪", " "]);

        Assert.Single(matcher.Entries);
    }
}

