using ZeOverlay.Shared;

namespace ZeOverlay.Stage.Matching;

/// <summary>S6 实现：名单过滤/纠错 + 排序。</summary>
public sealed class MatchingStage : IMatchingStage
{
    private readonly Matcher _matcher;
    private readonly IReadOnlyList<string> _sortOrder;

    public MatchingStage(Matcher matcher, IReadOnlyList<string>? sortOrder = null)
    {
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _sortOrder = sortOrder ?? [];
    }

    public string Name => "名单匹配";

    public IReadOnlyList<DisplayEntry> Process(TrackingResult input)
    {
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < _sortOrder.Count; i++)
        {
            ranks[_sortOrder[i]] = i;
        }

        var result = new List<DisplayEntry>();

        foreach (TrackerEntryView entry in input.Entries)
        {
            WatchlistMatch? match = _matcher.Match(entry.ArtifactName);
            if (!_matcher.IsEmpty && match is null)
            {
                continue;
            }

            result.Add(new DisplayEntry(
                entry.Page,
                entry.Slot,
                match?.CanonicalName ?? entry.ArtifactName,
                entry.State,
                entry.CooldownSeconds,
                entry.UsesRemaining,
                entry.UsesTotal,
                entry.Source,
                entry.ServerIndex));
        }

        return result
            .OrderBy(e => ranks.TryGetValue(e.Name, out int rank) ? rank : int.MaxValue)
            .ThenBy(e => (int)e.Page)
            .ThenBy(e => e.Slot)
            .ToList();
    }
}
