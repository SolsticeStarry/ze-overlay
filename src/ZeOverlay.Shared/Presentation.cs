namespace ZeOverlay.Shared;

/// <summary>S7 的输入：帧 + 待展示条目 + 状态文本。</summary>
public sealed record PresentationInput(
    ImageFrame Frame,
    IReadOnlyList<DisplayEntry> Entries,
    string Status,
    string EngineLabel,
    bool HasWatchlist);

/// <summary>S7 的输出：本次要显示的识别文本（供预览窗口复用）。</summary>
public sealed record PresentationResult(string RecognitionText);

/// <summary>叠加/识别的显示排序方式（M5 配置项）。</summary>
public enum OverlaySortMode
{
    /// <summary>按名单里的显示顺序，名单外条目排在最后（并保持页/行序）。默认。</summary>
    Watchlist,

    /// <summary>纯按「页 → 行槽位」，即列表原始顺序。</summary>
    Slot,

    /// <summary>就绪在前；同为冷却时按剩余秒数升序。</summary>
    Cooldown,

    /// <summary>按显示名称（名单标准名优先）字典序。</summary>
    Name,
}

/// <summary>展示排序的纯逻辑，便于单测（Host 只负责取名称与名单名次）。</summary>
public static class OverlaySort
{
    public static OverlaySortMode Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "slot" => OverlaySortMode.Slot,
        "cooldown" => OverlaySortMode.Cooldown,
        "name" => OverlaySortMode.Name,
        _ => OverlaySortMode.Watchlist,
    };

    /// <summary>
    /// 稳定性约定：除 <see cref="OverlaySortMode.Watchlist"/> 外的模式都以「页 → 行槽位」收尾，
    /// 保证同键条目顺序确定、不随帧抖动。
    /// </summary>
    /// <param name="nameOf">条目用于排序/查名次的显示名（命中名单时为标准名，否则为识别名）。</param>
    public static List<TrackerEntryView> Apply(
        IEnumerable<TrackerEntryView> entries,
        OverlaySortMode mode,
        IReadOnlyDictionary<string, int> watchlistRanks,
        Func<TrackerEntryView, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(nameOf);

        return mode switch
        {
            OverlaySortMode.Slot => entries
                .OrderBy(e => (int)e.Page)
                .ThenBy(e => e.Slot)
                .ToList(),

            OverlaySortMode.Name => entries
                .OrderBy(e => nameOf(e), StringComparer.Ordinal)
                .ThenBy(e => (int)e.Page)
                .ThenBy(e => e.Slot)
                .ToList(),

            OverlaySortMode.Cooldown => entries
                .OrderBy(e => e.State == ArtifactState.Cooling ? 1 : 0)
                .ThenBy(e => e.State == ArtifactState.Cooling ? e.CooldownSeconds ?? int.MaxValue : 0)
                .ThenBy(e => (int)e.Page)
                .ThenBy(e => e.Slot)
                .ToList(),

            _ => entries
                .OrderBy(e => watchlistRanks.TryGetValue(nameOf(e), out int rank) ? rank : int.MaxValue)
                .ThenBy(e => (int)e.Page)
                .ThenBy(e => e.Slot)
                .ToList(),
        };
    }
}

/// <summary>
/// 行号相关纯逻辑：判断名称是否**已带数字标号**（如 `1 滋水枪`、`【2】滋水枪`）。
/// 当前社区的名称为空标号 ⇒ 展示时自动按行从 1 编号；将来社区自带标号则不再叠加。
/// </summary>
public static class RowLabels
{
    public static bool HasNumberLabel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        int i = 0;
        while (i < name.Length && char.IsWhiteSpace(name[i]))
        {
            i++;
        }

        // 可选的开括号（半角/全角）
        if (i < name.Length && "([{【（［〔".IndexOf(name[i]) >= 0)
        {
            i++;
        }

        int start = i;
        while (i < name.Length && char.IsDigit(name[i]))
        {
            i++;
        }

        return i > start;
    }
}
