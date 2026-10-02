using ZeOverlay.Infrastructure;
using ZeOverlay.Shared;
using ZeOverlay.Stage.Matching;
using ZeOverlay.Stage.Parsing;
using ZeOverlay.Stage.Tracking;

namespace ZeOverlay.Tests.Scenarios;

/// <summary>模拟的社区服。</summary>
public enum ServerKind
{
    /// <summary>EXG 社区服：`名称 [R]/[数字] 可选n/m 玩家名`，行槽位身份、有翻页（单页 12 行）。</summary>
    Exg,

    /// <summary>FYS 社区服（entWatch）：`简称+标号 玩家名 状态`，标号身份、无翻页、会回流（单页 16 行）。</summary>
    Fys,
}

/// <summary>
/// 用一个「假 HUD」驱动**真实识别管线**（S4 解析 → S5 跟踪 → S6 匹配）：
/// 每个 <see cref="Feed"/> 代表一帧，给一组自上而下的原始 OCR 行文本，
/// 测试据此断言跟踪/倒计时结果。不需要图片、模型或 GUI。
///
/// 两个社区服共用同一套逐行自动分流逻辑（<see cref="Parser.ParseAuto"/>），
/// 只按 <see cref="ServerKind"/> 配不同的 <see cref="TrackerOptions"/>：
/// EXG 括号格式有翻页、单页 12 行；FYS 连写格式无翻页、单页 16 行。
/// 因此这里模拟的正是「同一条管线遇到两种服」的真实情况，而不是两套实现。
///
/// S5 直接调 <see cref="Tracker"/>（与 <see cref="TrackingStage"/> 内部同一套
/// ParsedRow→ObservedRow 映射），以便拿到带 Added/Removed 的 <see cref="TrackerFrameResult"/>。
/// </summary>
public sealed class ServerSimulator
{
    private static readonly DateTimeOffset DefaultStart = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly ParsingStage _parsing;
    private readonly Tracker _tracker;
    private readonly MatchingStage _matching;
    private readonly DateTimeOffset _start;

    public ServerSimulator(ServerKind kind, DateTimeOffset? start = null, double disappearAfterSeconds = 6.0)
    {
        Kind = kind;
        _start = start ?? DefaultStart;

        IReadOnlyList<string> vocabulary = kind == ServerKind.Fys
            ? ProfileDefaults.PlainVocabulary
            : ProfileDefaults.BracketVocabulary;

        _parsing = new ParsingStage(vocabulary: vocabulary);

        var options = kind == ServerKind.Fys
            ? new TrackerOptions { PagingEnabled = false, MaxRowsPerPage = 16, DisappearAfterSeconds = disappearAfterSeconds }
            : new TrackerOptions { PagingEnabled = true, MaxRowsPerPage = ListRules.MaxRowsPerPage, DisappearAfterSeconds = disappearAfterSeconds };

        _tracker = new Tracker(options);
        // 名单为空 = 全部显示：避免匹配器过滤影响跟踪断言。
        _matching = new MatchingStage(new Matcher(null));
    }

    public ServerKind Kind { get; }

    /// <summary>当前模拟时钟（由最近一次 Feed / SnapshotAt 决定）。</summary>
    public DateTimeOffset Now { get; private set; }

    /// <summary>最近一帧经 S6 名单匹配后的展示条目（名单为空时全部显示）。</summary>
    public IReadOnlyList<DisplayEntry> LastDisplay { get; private set; } = [];

    /// <summary>推进到 <c>start + atSeconds</c> 并喂入一帧 HUD 文本（参数顺序 = 自上而下的行）。</summary>
    public TrackerFrameResult Feed(double atSeconds, params string[] lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        Now = _start.AddSeconds(atSeconds);

        var recognized = new List<RecognizedRow>(lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            recognized.Add(new RecognizedRow(i, lines[i], 0.95));
        }

        IReadOnlyList<ParsedRow> parsed = _parsing.Process(recognized);

        var observed = new List<ObservedRow>(parsed.Count);
        foreach (ParsedRow row in parsed)
        {
            observed.Add(new ObservedRow(
                row.Slot,
                row.ArtifactName,
                row.State,
                row.CooldownSeconds,
                row.UsesRemaining,
                row.UsesTotal,
                row.PlayerName,
                row.ServerIndex));
        }

        TrackerFrameResult frame = _tracker.Observe(lines.Length, observed, Now);
        LastDisplay = _matching.Process(new TrackingResult(frame.Page, _tracker.Snapshot(Now), null));
        return frame;
    }

    /// <summary>当前跟踪条目快照。</summary>
    public IReadOnlyList<TrackerEntryView> Snapshot() => _tracker.Snapshot(Now);

    /// <summary>不喂新帧，只把墙钟推进到 <c>start + atSeconds</c> 后取快照（用于外推/倒计时断言）。</summary>
    public IReadOnlyList<TrackerEntryView> SnapshotAt(double atSeconds)
    {
        Now = _start.AddSeconds(atSeconds);
        return _tracker.Snapshot(Now);
    }
}
