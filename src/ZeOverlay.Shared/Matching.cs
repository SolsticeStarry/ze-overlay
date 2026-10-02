namespace ZeOverlay.Shared;

/// <summary>一次名单命中。<see cref="CanonicalName"/> 是名单里的标准写法。</summary>
public sealed record WatchlistMatch(string CanonicalName, string ObservedName, double Score);

public sealed class WatchlistOptions
{
    /// <summary>模糊匹配阈值，默认 0.85（优先保证不误报）。</summary>
    public double Threshold { get; set; } = 0.85;

    /// <summary>最佳候选必须比次佳好出这么多，否则视为不明确、不命中。</summary>
    public double Margin { get; set; } = 0.05;
}

/// <summary>S6 的输出：一条待展示的条目（已排序、已按名单纠正）。</summary>
public sealed record DisplayEntry(
    VisiblePage Page,
    int Slot,
    string Name,
    ArtifactState State,
    int? CooldownSeconds,
    int? UsesRemaining,
    int? UsesTotal,
    EntrySource Source)
{
    public string Key => $"P{(int)Page}#{Slot}";
}
