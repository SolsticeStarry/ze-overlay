using ZeOverlay.Shared;

namespace ZeOverlay.Stage.Tracking;

/// <summary>
/// 按**行槽位**跟踪。
///
/// 身份 = `(页, 行号)`，**不用神器名或玩家名做身份**。理由（用户实测反馈）：
/// 1. 神器会经常性换玩家，用「名称+玩家名」做 key 时，单帧无法判断是「旧条目更新」还是「全新条目」；
/// 2. 玩家名的 OCR 错字会让同一行每帧换一个 key，造成大量重复条目；
/// 3. 列表**顶部锚定、行距固定**，行槽位本身就是稳定身份 —— 行号相同就是同一行，
///    这一行的读数只更新这一行。
///
/// 消亡规则：某槽位超过 <see cref="TrackerOptions.DisappearAfterSeconds"/>（默认 5s）
/// 没扫到数据就**自动移除**——**无条件**，不分页、不看是否可见。
/// 也就是说翻到别的页 / 列表消失后，旧条目最多再保留 DisappearAfterSeconds 秒即消失。
/// </summary>
public sealed class Tracker
{
    private sealed class Entry
    {
        public required VisiblePage Page { get; init; }

        public required int Slot { get; set; }

        public required string Key { get; init; }

        public int? ServerIndex { get; set; }

        public string ArtifactName { get; set; } = string.Empty;

        public string PlayerName { get; set; } = string.Empty;

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

    private readonly TrackerOptions _options;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _baselineRows;

    public Tracker(TrackerOptions? options = null)
    {
        _options = options ?? new TrackerOptions();
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
                entry.MissedSessions,
                entry.PlayerName,
                entry.ServerIndex));
        }

        return views;
    }

    public TrackerFrameResult Observe(int rowCount, IReadOnlyList<ObservedRow> rows, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rows);

        // 社区服每行带服务器标号 ⇒ 用「名称+标号」身份且不分页；本服无标号 ⇒ 行槽位身份。
        // **粘性**：只要还有标号身份条目存在，就保持标号模式，避免"某一帧标号全丢"时
        // 翻成行槽位身份、造出槽位键条目污染（真正换到无标号服时，旧标号条目超时会自然退出）。
        bool anyLabel = rows.Any(r => r.ServerIndex is not null);
        bool hasLabelEntries = _entries.Values.Any(e => e.ServerIndex is not null);
        bool byLabel = _options.KeyByServerLabel || anyLabel || hasLabelEntries;

        VisiblePage page = ClassifyPage(rowCount, byLabel);

        var added = new List<string>();
        var refreshed = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (ObservedRow row in rows)
        {
            // 标号身份：**直接采信画面读数**。标号整段读丢（null）时**不猜**——
            // 按「同名最近槽位」借标号会在同类多神器下污染真实兄弟（跨帧也会），
            // 故宁可跳过这一行（缺一帧数据，最多 6s 后由画面重读）。EXG 无标号走行槽位身份。
            int? labelIndex = row.ServerIndex;
            if (byLabel && labelIndex is null)
            {
                continue;
            }

            string key = byLabel
                ? $"{(int)page}:{row.ArtifactName}:{labelIndex}"
                : $"{(int)page}:{row.Slot}";

            if (!_entries.TryGetValue(key, out Entry? entry))
            {
                entry = new Entry
                {
                    Page = page,
                    Slot = row.Slot,
                    Key = key,
                    ServerIndex = labelIndex,
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
            bool sameArtifact = string.Equals(entry.ArtifactName, row.ArtifactName, StringComparison.Ordinal)
                && entry.ServerIndex == row.ServerIndex;

            int? previousCooldown = ExtrapolatedCooldown(entry, now);
            bool wasCooling = entry.State == ArtifactState.Cooling;

            entry.ArtifactName = row.ArtifactName;
            entry.PlayerName = row.PlayerName ?? string.Empty;
            entry.ServerIndex = labelIndex;
            entry.Slot = row.Slot;
            entry.State = row.State;

            if (row.State == ArtifactState.Cooling)
            {
                int newCooldown = Math.Max(0, row.CooldownSeconds ?? 0);

                // 冷却只会往下走。同一神器上读数突然远大于外推值，多半是 OCR 把小数读丢
                // （`3.5s` → `35s`）或读花；直接采纳会让倒计时乱跳 ⇒ 忽略这次尖峰，沿用外推。
                //
                // 例外：外推值已接近 0（冷却基本走完）时的**再次使用**会给出一个新的长冷却，
                // 这是合法重置，必须接受，否则会一直卡在"就绪"（1Hz 采样可能看不到中间的 R 帧）。
                // 例外1：上一帧没连续观测到（中间有间断）⇒ 可能是"新一件复用了同一身份"或"再次使用"，
                //        无法用"只降不升"判断，直接采信读数。
                // 例外2：外推值已接近 0（冷却基本走完）时的再次使用也会给出新的长冷却，是合法重置。
                bool plausible = entry.Source != EntrySource.Live
                    || !wasCooling
                    || !sameArtifact
                    || previousCooldown is not { } previous
                    || previous <= 1
                    || newCooldown <= previous + 1;

                entry.CooldownAtObservation = plausible ? newCooldown : previousCooldown;
            }
            else
            {
                entry.CooldownAtObservation = null;
            }

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
            // 无条件超时：不分页、不看是否可见——超过时限没扫到就移除。
            bool timedOut = (now - entry.LastSeen).TotalSeconds > _options.DisappearAfterSeconds;
            bool tooOld = (now - entry.LastSeen).TotalSeconds > _options.MaxAgeSeconds;

            if (timedOut || tooOld)
            {
                _entries.Remove(entry.Key);
                removed.Add(entry.Display);
            }
        }

        return new TrackerFrameResult(page, rowCount, added, refreshed, missed, removed);
    }

    private int? ExtrapolatedCooldown(Entry entry, DateTimeOffset now)
    {
        if (entry.CooldownAtObservation is not { } observed)
        {
            return null;
        }

        double elapsed = (now - entry.LastSeen).TotalSeconds;
        return Math.Max(0, (int)Math.Ceiling(observed - elapsed));
    }

    private VisiblePage ClassifyPage(int rowCount, bool byLabel)
    {
        if (rowCount <= 0)
        {
            return VisiblePage.Unknown;
        }

        // 社区服（带标号）实测不翻页：所有可见行都算同一页，不产生第 2 页身份。
        if (byLabel || !_options.PagingEnabled)
        {
            return VisiblePage.Page1;
        }

        _baselineRows = Math.Max(_baselineRows, Math.Min(rowCount, _options.MaxRowsPerPage));

        if (_baselineRows <= 0)
        {
            return VisiblePage.Unknown;
        }

        double threshold = Math.Ceiling(_baselineRows * _options.PageOneRowRatio);
        return rowCount >= threshold ? VisiblePage.Page1 : VisiblePage.Page2;
    }
}
