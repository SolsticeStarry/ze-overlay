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

/// <summary>一行的识别结果。**只带行号，不带玩家名**。</summary>
public sealed record ObservedRow(
    int Slot,
    string ArtifactName,
    ArtifactState State,
    int? CooldownSeconds = null,
    int? UsesRemaining = null,
    int? UsesTotal = null);

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
    int MissedSessions)
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

public sealed class TrackerOptions
{
    /// <summary>连续多少次「该在的页可见却没读到这一行」之后移除槽位。</summary>
    public int MaxConsecutiveMisses { get; set; } = 3;

    /// <summary>miss 的最小间隔（秒）：按时间桶计，而不是按帧计。</summary>
    public double MinMissIntervalSeconds { get; set; } = 2.0;

    /// <summary>行数不低于基准行数的这个比例时，认为当前显示的是第 1 页。</summary>
    public double PageOneRowRatio { get; set; } = 0.6;

    /// <summary>兜底：距最后一次读到超过这么久就移除。</summary>
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
