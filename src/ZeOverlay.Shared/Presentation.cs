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
    /// 稳定性约定：各模式都以「页 → 标号 → 行槽位」收尾（槽位只作最后兜底）。
    /// **标号优先于槽位**很关键：连写服回流会让条目保留"上次所在行位"，同名多神器
    /// （爆闪1/爆闪2）会因行位互换而上下颠倒；按标号排就稳定。
    /// <see cref="OverlaySortMode.Slot"/> 例外，保持"原始行序"。
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
                .ThenBy(e => e.ServerIndex ?? int.MaxValue)
                .ThenBy(e => e.Slot)
                .ToList(),

            OverlaySortMode.Cooldown => entries
                .OrderBy(e => e.State == ArtifactState.Cooling ? 1 : 0)
                .ThenBy(e => e.State == ArtifactState.Cooling ? e.CooldownSeconds ?? int.MaxValue : 0)
                .ThenBy(e => (int)e.Page)
                .ThenBy(e => e.ServerIndex ?? int.MaxValue)
                .ThenBy(e => e.Slot)
                .ToList(),

            _ => entries
                .OrderBy(e => watchlistRanks.TryGetValue(nameOf(e), out int rank) ? rank : int.MaxValue)
                .ThenBy(e => (int)e.Page)
                .ThenBy(e => e.ServerIndex ?? int.MaxValue)
                .ThenBy(e => e.Slot)
                .ToList(),
        };
    }
}

/// <summary>
/// 行号相关纯逻辑：判断名称是否**已带数字标号**（如 `滋水枪 3`、`3 滋水枪`、`【2】滋水枪`）。
/// 当前社区的名称为空标号 ⇒ 展示时在名称后自动补 1,2,3…；将来社区自带标号则不再叠加。
/// </summary>
public static class RowLabels
{
    public static bool HasNumberLabel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return HasLeadingNumber(name) || HasTrailingNumber(name);
    }

    private static bool HasLeadingNumber(string name)
    {
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

    private static bool HasTrailingNumber(string name)
    {
        int i = name.Length - 1;
        while (i >= 0 && char.IsWhiteSpace(name[i]))
        {
            i--;
        }

        // 可选的闭括号（半角/全角）
        if (i >= 0 && ")]}】）］〕".IndexOf(name[i]) >= 0)
        {
            i--;
        }

        int end = i;
        while (i >= 0 && char.IsDigit(name[i]))
        {
            i--;
        }

        return i < end;
    }
}
