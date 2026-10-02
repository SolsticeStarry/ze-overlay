using ZeOverlay.Shared;

namespace ZeOverlay.Tests.Presentation;

public sealed class OverlaySortTests
{
    private static TrackerEntryView Entry(
        VisiblePage page,
        int slot,
        string name,
        ArtifactState state = ArtifactState.Ready,
        int? cooldown = null,
        EntrySource source = EntrySource.Live)
        => new(
            page,
            slot,
            name,
            state,
            cooldown,
            null,
            null,
            source,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            0);

    private static readonly IReadOnlyDictionary<string, int> EmptyRanks =
        new Dictionary<string, int>(StringComparer.Ordinal);

    private static Func<TrackerEntryView, string> NameOf => e => e.ArtifactName;

    [Theory]
    [InlineData("slot", OverlaySortMode.Slot)]
    [InlineData("cooldown", OverlaySortMode.Cooldown)]
    [InlineData("name", OverlaySortMode.Name)]
    [InlineData("watchlist", OverlaySortMode.Watchlist)]
    [InlineData("WATCHLIST", OverlaySortMode.Watchlist)]
    [InlineData("unknown", OverlaySortMode.Watchlist)]
    [InlineData(null, OverlaySortMode.Watchlist)]
    public void Parse_MapsKnownAndUnknown(string? text, OverlaySortMode expected)
        => Assert.Equal(expected, OverlaySort.Parse(text));

    [Fact]
    public void Slot_OrdersByPageThenSlot()
    {
        var entries = new[]
        {
            Entry(VisiblePage.Page2, 1, "B"),
            Entry(VisiblePage.Page1, 5, "C"),
            Entry(VisiblePage.Page1, 2, "A"),
        };

        var ordered = OverlaySort.Apply(entries, OverlaySortMode.Slot, EmptyRanks, NameOf);

        Assert.Equal(["A", "C", "B"], ordered.Select(e => e.ArtifactName));
    }

    [Fact]
    public void Name_OrdersOrdinal()
    {
        var entries = new[]
        {
            Entry(VisiblePage.Page1, 1, "滋水枪"),
            Entry(VisiblePage.Page1, 2, "bag"),
            Entry(VisiblePage.Page1, 3, "Apple"),
        };

        var ordered = OverlaySort.Apply(entries, OverlaySortMode.Name, EmptyRanks, NameOf);

        // Ordinal：大写字母 < 小写字母 < 汉字。
        Assert.Equal(["Apple", "bag", "滋水枪"], ordered.Select(e => e.ArtifactName));
    }

    [Fact]
    public void Cooldown_ReadyFirstThenAscendingSeconds()
    {
        var entries = new[]
        {
            Entry(VisiblePage.Page1, 1, "cool-30", ArtifactState.Cooling, 30),
            Entry(VisiblePage.Page1, 2, "ready-b", ArtifactState.Ready),
            Entry(VisiblePage.Page1, 3, "cool-5", ArtifactState.Cooling, 5),
            Entry(VisiblePage.Page1, 4, "ready-a", ArtifactState.Ready),
        };

        var ordered = OverlaySort.Apply(entries, OverlaySortMode.Cooldown, EmptyRanks, NameOf);

        // 就绪在前且内部按页/行序，再按冷却秒数升序。
        Assert.Equal(["ready-b", "ready-a", "cool-5", "cool-30"], ordered.Select(e => e.ArtifactName));
    }

    [Fact]
    public void Watchlist_UsesRankThenPageSlot_UnknownLast()
    {
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["饮水"] = 0,
            ["袋装"] = 1,
        };

        var entries = new[]
        {
            Entry(VisiblePage.Page1, 3, "未知"),
            Entry(VisiblePage.Page1, 2, "袋装"),
            Entry(VisiblePage.Page2, 1, "饮水"),
        };

        var ordered = OverlaySort.Apply(entries, OverlaySortMode.Watchlist, ranks, NameOf);

        Assert.Equal(["饮水", "袋装", "未知"], ordered.Select(e => e.ArtifactName));
    }

    [Fact]
    public void Watchlist_SameRankFallsBackToPageSlot()
    {
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["同类"] = 0,
        };

        var entries = new[]
        {
            Entry(VisiblePage.Page1, 5, "同类"),
            Entry(VisiblePage.Page1, 1, "同类"),
            Entry(VisiblePage.Page2, 1, "同类"),
        };

        var ordered = OverlaySort.Apply(entries, OverlaySortMode.Watchlist, ranks, NameOf);

        Assert.Equal(
            [(VisiblePage.Page1, 1), (VisiblePage.Page1, 5), (VisiblePage.Page2, 1)],
            ordered.Select(e => (e.Page, e.Slot)));
    }

    [Fact]
    public void NameOf_UsesCanonicalNameForWatchlistRanking()
    {
        // 识别名与名单标准名不同：排序必须按标准名查名次。
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["标准名"] = 0,
        };

        TrackerEntryView entry = Entry(VisiblePage.Page1, 9, "识别名");
        var ordered = OverlaySort.Apply([entry], OverlaySortMode.Watchlist, ranks, _ => "标准名");

        Assert.Single(ordered);
    }
}
