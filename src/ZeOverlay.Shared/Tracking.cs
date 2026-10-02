namespace ZeOverlay.Shared;

public enum ArtifactState
{
    Unknown,

    /// <summary>就绪（`[R]`）。</summary>
    Ready,

    /// <summary>冷却中（`[数字]`）。</summary>
    Cooling,
}

public enum EntrySource
{
    /// <summary>画面上实时读到。</summary>
    Live,

    /// <summary>本地外推（被翻走 / 列表消失）。</summary>
    Extrapolated,
}

/// <summary>当前可见的是哪一页。</summary>
public enum VisiblePage
{
    Unknown,

    /// <summary>行数接近基准（满页）。</summary>
    Page1,

    /// <summary>行数明显少于基准（第 2 页）。</summary>
    Page2,
}

/// <summary>一行的识别结果。默认身份只用行号；玩家名仅作展示（不参与身份）。</summary>
public sealed record ObservedRow(
    int Slot,
    string ArtifactName,
    ArtifactState State,
    int? CooldownSeconds = null,
    int? UsesRemaining = null,
    int? UsesTotal = null,
    string PlayerName = "",
    int? ServerIndex = null);

/// <summary>对外展示的条目（倒计时已按墙钟外推）。</summary>
public sealed record TrackerEntryView(
    VisiblePage Page,
    int Slot,
    string ArtifactName,
    ArtifactState State,
    int? CooldownSeconds,
    int? UsesRemaining,
    int? UsesTotal,
    EntrySource Source,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    int MissedSessions,
    string PlayerName = "",
    int? ServerIndex = null)
{
    public string Display => $"{ArtifactName} (第{(int)Page}页 #{Slot})";
}

public sealed record TrackerFrameResult(
    VisiblePage Page,
    int RowCount,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Refreshed,
    IReadOnlyList<string> Missed,
    IReadOnlyList<string> Removed);

/// <summary>
/// 跟踪条目的**稳定键**（名单匹配缓存 / 排序用）。
/// 标号身份（连写服）用「名称+标号」；行槽位身份用「页+槽位」。
/// 关键：标号身份下条目会保留"上次所在行位"，不同条目可能撞同一行位，
/// 所以**不能**用 页#槽位 当键，否则名单匹配会张冠李戴。
/// </summary>
public static class TrackerKeys
{
    public static string Identity(TrackerEntryView entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.ServerIndex is { } index
            ? $"{entry.ArtifactName}\u0000{index}"
            : $"P{(int)entry.Page}#{entry.Slot}";
    }
}

public sealed class TrackerOptions
{
    /// <summary>连续多少次「该在的页可见却没读到这一行」之后移除槽位（诊断用，实际移除以时限为准）。</summary>
    public int MaxConsecutiveMisses { get; set; } = 3;

    /// <summary>miss 的最小间隔（秒）：按时间桶计，而不是按帧计。</summary>
    public double MinMissIntervalSeconds { get; set; } = 2.0;

    /// <summary>
    /// 自动消失时限（秒）：某槽位超过这么久没扫到数据就移除。默认 6s。
    /// **无条件**——翻到别的页或列表消失后，旧条目也最多再保留这么久。
    /// </summary>
    public double DisappearAfterSeconds { get; set; } = 6.0;

    /// <summary>行数不低于基准行数的这个比例时，认为当前显示的是第 1 页。</summary>
    public double PageOneRowRatio { get; set; } = 0.6;

    /// <summary>
    /// 该服务器档案是否有翻页。为 false（社区服实测无翻页）时，不再区分第 1/2 页，
    /// 所有可见行都当作同一页，避免行数波动被误判成翻页。
    /// </summary>
    public bool PagingEnabled { get; set; } = true;

    /// <summary>该档案的单页最大行数（用于钳制基准行数）。</summary>
    public int MaxRowsPerPage { get; set; } = ListRules.MaxRowsPerPage;

    /// <summary>
    /// 身份改用「神器名 + 服务器标号」（<see cref="ObservedRow.ServerIndex"/>）而不是行槽位。
    /// 社区服列表在神器被用掉后会回流（下方整体上移），槽位不再稳定，必须用标号身份。
    /// 本服括号语法没有标号，保持 false（行槽位身份）。
    /// </summary>
    public bool KeyByServerLabel { get; set; }

    /// <summary>兜底：距最后一次读到超过这么久就移除（含不可见页/列表消失的情况）。</summary>
    public int MaxAgeSeconds { get; set; } = 300;
}

/// <summary>一帧的列表结构快照。</summary>
public sealed record ListStructureSnapshot(int RowCount, double? MedianPitch, string Signature)
{
    public static ListStructureSnapshot FromBands(IReadOnlyList<RowBand> bands, double? medianPitch)
    {
        ArgumentNullException.ThrowIfNull(bands);
        return new ListStructureSnapshot(bands.Count, medianPitch, BuildSignature(bands));
    }

    /// <summary>内容签名。**必须量化**（行顶按 2 px、行宽按 8 px 分桶），否则抗锯齿会让签名每帧抖动。</summary>
    public static string BuildSignature(IReadOnlyList<RowBand> bands)
    {
        ArgumentNullException.ThrowIfNull(bands);

        return string.Join(
            "|",
            bands.Select(b => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{b.Top / 2}:{b.Width / 8}")));
    }
}

public enum ListChangeKind
{
    /// <summary>行数变了。</summary>
    RowCountChanged,

    /// <summary>行数不变但内容变了。</summary>
    ContentChanged,

    /// <summary>两者同时变。</summary>
    RowCountAndContentChanged,
}

/// <summary>一次列表变化。</summary>
public sealed record ListChange(
    ListChangeKind Kind,
    int PreviousRowCount,
    int CurrentRowCount,
    bool LooksLikePageChange);

/// <summary>S5 的输入。</summary>
public sealed record TrackingInput(int RowCount, IReadOnlyList<ParsedRow> Rows, DateTimeOffset Now);

/// <summary>S5 的输出。</summary>
public sealed record TrackingResult(
    VisiblePage Page,
    IReadOnlyList<TrackerEntryView> Entries,
    ListChange? Change);
