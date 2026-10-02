using ZeOverlay.Shared;
using ZeOverlay.Infrastructure;

namespace ZeOverlay.Tests.Profiles;

/// <summary>
/// 旧配置（全局 ROI + watchlist.json）迁移到多档案模型。
/// </summary>
public sealed class ProfileMigrationTests
{
    [Fact]
    public void EnsureProfiles_MigratesLegacyRoiAndWatchlist()
    {
        var config = new AppConfig
        {
            Roi = new RoiConfig { IsSet = true, ScreenRect = "10,20,300,400" },
        };
        var watchlist = new WatchlistConfig
        {
            Names = ["滋水枪"],
            SortOrder = ["滋水枪"],
            MatchThreshold = 0.9,
        };

        ServerProfileConfig active = ProfileDefaults.EnsureProfiles(config, watchlist);

        Assert.Equal("default", active.Id);
        Assert.Equal("10,20,300,400", active.Roi.ScreenRect);
        Assert.Equal(new[] { "滋水枪" }, active.Watchlist);
        Assert.Equal(0.9, active.MatchThreshold);
        Assert.Equal("default", config.ActiveProfileId);
    }

    [Fact]
    public void EnsureProfiles_SeedsCommunityProfile_PlainNoPaging()
    {
        var config = new AppConfig();
        ProfileDefaults.EnsureProfiles(config, new WatchlistConfig());

        ServerProfileConfig community = config.Profiles.Single(p => p.Id == "community");
        Assert.Equal("plain", community.ParserMode);
        Assert.False(community.PagingEnabled);
        Assert.NotEmpty(community.Vocabulary);
        Assert.False(community.Roi.IsSet);

        // 社区服列表变化快：采集/识别更快，行消失超时继承全局（保留 6s 粘滞）。
        Assert.Equal(5, community.CaptureFps);
        Assert.Equal(200, community.RecognitionIntervalMs);
        Assert.Equal(0, community.RowDisappearSeconds);
    }

    [Fact]
    public void EnsureProfiles_IsIdempotent()
    {
        var config = new AppConfig();
        ProfileDefaults.EnsureProfiles(config, new WatchlistConfig());
        int first = config.Profiles.Count;

        ServerProfileConfig active = ProfileDefaults.EnsureProfiles(config, new WatchlistConfig());

        Assert.Equal(first, config.Profiles.Count);
        Assert.Equal("default", active.Id);
    }

    [Fact]
    public void ApplyWatchlist_RoundTrips()
    {
        var profile = new ServerProfileConfig { Id = "x" };
        ProfileDefaults.ApplyWatchlist(
            profile,
            new WatchlistConfig { Names = ["手电"], SortOrder = ["手电"], MatchThreshold = 0.8 });

        WatchlistConfig result = ProfileDefaults.ToWatchlist(profile);

        Assert.Equal(new[] { "手电" }, result.Names);
        Assert.Equal(new[] { "手电" }, result.SortOrder);
        Assert.Equal(0.8, result.MatchThreshold);
    }

    [Theory]
    [InlineData("plain", ParserMode.Plain)]
    [InlineData("PLAIN", ParserMode.Plain)]
    [InlineData("bracket", ParserMode.Bracket)]
    [InlineData("", ParserMode.Bracket)]
    [InlineData(null, ParserMode.Bracket)]
    public void ParseMode_MapsText(string? text, ParserMode expected)
        => Assert.Equal(expected, ProfileDefaults.ParseMode(text));
}
