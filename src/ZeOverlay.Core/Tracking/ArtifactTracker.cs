namespace ZeOverlay.Core.Tracking;

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

    /// <summary>行数明显少于基准（第 2 页，通常只有 1~3 行）。</summary>
    Page2,
}

/// <summary>
/// 一行的识别结果。**只带行号，不带玩家名**。
/// </summary>
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

public sealed class ArtifactTrackerOptions
{
    /// <summary>连续多少次「该在的页可见却没读到这一行」之后移除槽位。</summary>
    public int MaxConsecutiveMisses { get; set; } = 3;

    /// <summary>miss 的最小间隔（秒）。按时间桶计，而不是按帧计——2 Hz 下按帧计会瞬间判死所有槽位。</summary>
    public double MinMissIntervalSeconds { get; set; } = 2.0;

    /// <summary>行数不低于基准行数的这个比例时，认为当前显示的是第 1 页。</summary>
    public double PageOneRowRatio { get; set; } = 0.6;

    /// <summary>兜底：距最后一次读到超过这么久就移除。</summary>
    public int MaxAgeSeconds { get; set; } = 300;
}

/// <summary>
/// 按**行槽位**跟踪。
///
/// 身份 = `(页, 行号)`，**不用神器名或玩家名做身份**。理由（用户实测反馈）：
/// 1. 神器会经常性换玩家，用「名称+玩家名」做 key 时，单帧无法判断是「旧条目更新」还是「全新条目」；
/// 2. 玩家名的 OCR 错字会让同一行每帧换一个 key，造成大量重复条目；
/// 3. 列表**顶部锚定、行距固定**，行槽位本身就是稳定身份 —— 行号相同就是同一行，
///    这一行的读数只更新这一行。
///
/// 消亡规则：只有「该槽位所在页正可见、却读不到这一行」才记 miss，
/// 且 miss 按时间桶计一次；连续 N 次 ⇒ 移除。
/// </summary>
public sealed class ArtifactTracker
{
    private sealed class Entry
    {
        public required VisiblePage Page { get; init; }

        public required int Slot { get; init; }

        public required string Key { get; init; }

        public string ArtifactName { get; set; } = string.Empty;

        public ArtifactState State { get; set; } = ArtifactState.Unknown;

        public int? CooldownAtObservation { get; set; }

        public int? UsesRemaining { get; set; }

        public int? UsesTotal { get; set; }

        public EntrySource Source { get; set; } = EntrySource.Live;

        public DateTimeOffset FirstSeen { get; init; }

        public DateTimeOffset LastSeen { get; set; }

        public int MissedSessions { get; set; }

        public DateTimeOffset LastMissAt { get; set; } = DateTimeOffset.MinValue;

        public string Display => $"{ArtifactName} (第{(int)Page}页 #{Slot})";
    }

    private readonly ArtifactTrackerOptions _options;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _baselineRows;

    public ArtifactTracker(ArtifactTrackerOptions? options = null)
    {
        _options = options ?? new ArtifactTrackerOptions();
    }

    public int BaselineRows => _baselineRows;

    /// <summary>当前条目视图（倒计时按墙钟外推；外推归零即显示为 `[R]`）。</summary>
    public IReadOnlyList<TrackerEntryView> Snapshot(DateTimeOffset now)
    {
        var views = new List<TrackerEntryView>(_entries.Count);

        foreach (Entry entry in _entries.Values)
        {
            int? cooldown = ExtrapolatedCooldown(entry, now);
            ArtifactState state = entry.State;

            if (state == ArtifactState.Cooling && cooldown == 0)
            {
                state = ArtifactState.Ready;
            }

            views.Add(new TrackerEntryView(
                entry.Page,
                entry.Slot,
                entry.ArtifactName,
                state,
                cooldown,
                entry.UsesRemaining,
                entry.UsesTotal,
                entry.Source,
                entry.FirstSeen,
                entry.LastSeen,
                entry.MissedSessions));
        }

        return views;
    }

    public TrackerFrameResult Observe(int rowCount, IReadOnlyList<ObservedRow> rows, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rows);

        VisiblePage page = ClassifyPage(rowCount);

        var added = new List<string>();
        var refreshed = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (ObservedRow row in rows)
        {
            string key = KeyOf(page, row.Slot);

            if (!_entries.TryGetValue(key, out Entry? entry))
            {
                entry = new Entry
                {
                    Page = page,
                    Slot = row.Slot,
                    Key = key,
                    FirstSeen = now,
                    LastSeen = now,
                };

                _entries[key] = entry;
                added.Add(entry.Display);
            }
            else
            {
                refreshed.Add(entry.Display);
            }

            // 可见即重校：用画面上的读数整行覆盖。
            // 但 `n/m` 例外——实测 OCR 会间歇性漏读（`[R]4/5` 有时只读到 `[R]`），
            // 整行覆盖会让 uses 来回闪烁。所以**同一槽位同一神器时，没读到就保留旧值**；
            // 一旦该槽位换了神器（sameArtifact=false）仍然整行覆盖。
            bool sameArtifact = string.Equals(entry.ArtifactName, row.ArtifactName, StringComparison.Ordinal);

            entry.ArtifactName = row.ArtifactName;
            entry.State = row.State;
            entry.CooldownAtObservation = row.State == ArtifactState.Cooling ? row.CooldownSeconds : null;

            if (!sameArtifact || row.UsesRemaining is not null || row.UsesTotal is not null)
            {
                entry.UsesRemaining = row.UsesRemaining;
                entry.UsesTotal = row.UsesTotal;
            }

            entry.Source = EntrySource.Live;
            entry.LastSeen = now;
            entry.MissedSessions = 0;

            seen.Add(key);
        }

        var missed = new List<string>();

        foreach (Entry entry in _entries.Values)
        {
            if (seen.Contains(entry.Key))
            {
                continue;
            }

            entry.Source = EntrySource.Extrapolated;

            // 只有「该槽位所在页正可见」才可能判定它消失了；翻到另一页时不动它。
            if (page == VisiblePage.Unknown || entry.Page != page)
            {
                continue;
            }

            // 按时间桶计一次，避免 2 Hz 采样把槽位瞬间判死。
            if ((now - entry.LastMissAt).TotalSeconds < _options.MinMissIntervalSeconds)
            {
                continue;
            }

            entry.LastMissAt = now;
            entry.MissedSessions++;
            missed.Add(entry.Display);
        }

        var removed = new List<string>();

        foreach (Entry entry in _entries.Values.ToList())
        {
            bool tooManyMisses = entry.MissedSessions >= _options.MaxConsecutiveMisses;
            bool tooOld = (now - entry.LastSeen).TotalSeconds > _options.MaxAgeSeconds;

            if (tooManyMisses || tooOld)
            {
                _entries.Remove(entry.Key);
                removed.Add(entry.Display);
            }
        }

        return new TrackerFrameResult(page, rowCount, added, refreshed, missed, removed);
    }

    private static string KeyOf(VisiblePage page, int slot) => $"{(int)page}:{slot}";

    private int? ExtrapolatedCooldown(Entry entry, DateTimeOffset now)
    {
        if (entry.CooldownAtObservation is not { } observed)
        {
            return null;
        }

        double elapsed = (now - entry.LastSeen).TotalSeconds;
        return Math.Max(0, (int)Math.Ceiling(observed - elapsed));
    }

    private VisiblePage ClassifyPage(int rowCount)
    {
        if (rowCount <= 0)
        {
            return VisiblePage.Unknown;
        }

        _baselineRows = Math.Max(_baselineRows, Math.Min(rowCount, Domain.ListRules.MaxRowsPerPage));

        if (_baselineRows <= 0)
        {
            return VisiblePage.Unknown;
        }

        double threshold = Math.Ceiling(_baselineRows * _options.PageOneRowRatio);
        return rowCount >= threshold ? VisiblePage.Page1 : VisiblePage.Page2;
    }
}
