using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using ZeOverlay.Shared;
using ZeOverlay.Infrastructure;
using ZeOverlay.Win32;
using ZeOverlay.Stage.ScreenCapture;
using ZeOverlay.Stage.RowAnalysis;
using ZeOverlay.Stage.GlyphSegmentation;
using ZeOverlay.Stage.Recognition;
using ZeOverlay.Stage.Parsing;
using ZeOverlay.Stage.Tracking;
using ZeOverlay.Stage.Matching;
using ZeOverlay.Stage.Presentation;

namespace ZeOverlay.Gui;

public sealed partial class Host
{
    // Host 的「识别 → 解析 → 跟踪 → 名单」部分（partial）。
    private void AnalyzeAndRecognize(ImageFrame frame)
    {
        RowReport report;
        try
        {
            report = _rowAnalysisCache.Analyze(frame, image => Analyzer.Analyze(image));
        }
        catch (Exception ex)
        {
            _log?.Error("[分析] 行剖分失败", ex);
            return;
        }

        ObserveListStructure(report, frame);
        Recognize(report, frame);
    }

    /// <summary>
    /// 文字识别：OCR → 行解析 → 跨页跟踪 → 关注名单过滤。
    /// 这一条链路就是 PLAN 第 4 节的「识别层 → 解析层 → 跟踪层」。
    /// </summary>
    private void Recognize(RowReport report, ImageFrame frame)
    {
        string engineLabel = _pipeline is not null ? _recognitionName : (_recognizer?.Name ?? _ocr.Name);

        var observed = new List<ObservedRow>();
        var unparsed = new List<string>();

        // OCR 比采集贵得多（PP-OCR 12 行约 0.8s）。按 PLAN 第 10 节，采集 2Hz、识别低频，
        // 中间靠本地倒计时外推补上；间隔可由配置调整（M5：刷新频率）。
        if (DateTime.UtcNow - _lastOcrAt < CurrentOcrInterval())
        {
            BuildRecognitionText(DateTimeOffset.Now, engineLabel);
            return;
        }

        _lastOcrAt = DateTime.UtcNow;

        // 主路径：阶段化 Pipeline（S2 字形切分 → S3 识别 → S4 解析 → S5 跟踪 → S6 匹配）。
        if (_pipeline is not null)
        {
            var pipelineWatch = Stopwatch.StartNew();
            PipelineStepResult step = _pipeline.Step(frame, report, DateTimeOffset.Now);
            pipelineWatch.Stop();
            _lastOcrMs = pipelineWatch.Elapsed.TotalMilliseconds;

            foreach (DisplayEntry entry in step.Entries)
            {
                RecordRecentName(entry.Name);
            }

            DumpTrackerTable();
            BuildRecognitionText(DateTimeOffset.Now, _recognitionName);
            return;
        }

        IRowRecognizer? recognizer = _recognizer;
        if (recognizer is null)
        {
            return;
        }

        IReadOnlyList<RowBand> bands = report.GridBands.Count > 0 ? report.GridBands : report.Bands;

        // 统一走 IRowRecognizer：PP-OCR 与系统 OCR 走同一条路径，不再按引擎类型分叉。
        var watch = Stopwatch.StartNew();
        bool ok = recognizer.TryRecognizeRows(frame, bands, out IReadOnlyList<RecognizedRow> rows, out string? error);
        watch.Stop();
        _lastOcrMs = watch.Elapsed.TotalMilliseconds;

        if (!ok)
        {
            if (DateTime.UtcNow - _lastOcrWarnAt >= ContentChangeLogMinInterval)
            {
                _lastOcrWarnAt = DateTime.UtcNow;
                _log?.Warn($"[识别] OCR 失败：{error}");
            }

            return;
        }

        int sourceCount = rows.Count;

        foreach (RecognizedRow row in rows)
        {
            // 系统 OCR 无置信度（记 1.0），不会被这里刷掉；PP-OCR 的幻觉行会被挡下。
            if (row.Confidence < MinRowConfidence)
            {
                unparsed.Add($"{row.Text}（置信 {row.Confidence:0.00}，过低）");
                continue;
            }

            ParseInto(row.Text, row.Slot, observed, unparsed);
        }

        // 解析失败的行必须留证据：解析成功率低会让好条目反复被判 miss，是重复追踪的根源之一。
        if (unparsed.Count > 0 && DateTime.UtcNow - _lastUnparsedLogAt >= ContentChangeLogMinInterval)
        {
            _lastUnparsedLogAt = DateTime.UtcNow;
            _log?.Warn($"[识别] 有 {unparsed.Count} 行解析失败，样例：{string.Join(" ｜ ", unparsed.Take(4))}");
        }

        TrackerFrameResult result = _tracker.Observe(report.RowCount, observed, DateTimeOffset.Now);

        if (result.Added.Count > 0 || result.Removed.Count > 0)
        {
            _log?.Info(
                $"[识别] {engineLabel} 页={result.Page} 行数={report.RowCount} 解析={observed.Count}/{sourceCount} "
                + $"新增={result.Added.Count} 移除={result.Removed.Count} 耗时={_lastOcrMs:0}ms");
        }

        DumpTrackerTable();

        BuildRecognitionText(DateTimeOffset.Now, engineLabel);
    }

    /// <summary>
    /// 定期把整张跟踪表打进日志，用于定性「名单条目数不对」到底是真变化还是抖动。
    /// 只记键与状态，不记像素。
    /// </summary>
    private void DumpTrackerTable()
    {
        if (DateTime.UtcNow - _lastEntryDumpAt < EntryDumpInterval)
        {
            return;
        }

        _lastEntryDumpAt = DateTime.UtcNow;

        IReadOnlyList<TrackerEntryView> all = _tracker.Snapshot(DateTimeOffset.Now);
        int matched = all.Count(e => _watchlist.IsEmpty || _watchlist.Match(e.ArtifactName) is not null);

        string detail = string.Join(
            " | ",
            all.Select(e => string.Create(
                CultureInfo.InvariantCulture,
                $"P{(int)e.Page}#{e.Slot}={e.ArtifactName}"
                + $"{(e.State == ArtifactState.Cooling ? $"[{e.CooldownSeconds}]" : "[R]")}"
                + $"{(e.UsesRemaining is { } r && e.UsesTotal is { } t ? $"{r}/{t}" : string.Empty)}"
                + $" {(e.Source == EntrySource.Live ? "实" : "推")}m{e.MissedSessions}")));

        _log?.Info($"[跟踪表] 共 {all.Count} 条（命中名单 {matched} 条）: {detail}");
    }


    /// <summary>记录「最近识别到的神器名」，供设置窗口一键加入。</summary>
    private void RecordRecentName(string name)
    {
        lock (_recentNamesGate)
        {
            string trimmed = name.Trim();
            string? existing = _recentNameCounts.Keys
                .Where(candidate => IsRecentAlias(candidate, trimmed))
                .OrderByDescending(candidate => candidate.Length)
                .FirstOrDefault();
            _recentNameCounts[existing ?? trimmed] = _recentNameCounts.GetValueOrDefault(existing ?? trimmed) + 1;
        }
    }

    /// <summary>把一行识别文本解析成观测；失败时记入 unparsed 备查。玩家名不参与身份，直接丢弃。</summary>
    private void ParseInto(string text, int slot, List<ObservedRow> observed, List<string> unparsed)
    {
        ParsedRow? parsed = Parser.Parse(text);
        if (parsed is null)
        {
            unparsed.Add(text);
            return;
        }

        lock (_recentNamesGate)
        {
            string name = parsed.ArtifactName.Trim();
            string? existing = _recentNameCounts.Keys
                .Where(candidate => IsRecentAlias(candidate, name))
                .OrderByDescending(candidate => candidate.Length)
                .FirstOrDefault();
            _recentNameCounts[existing ?? name] = _recentNameCounts.GetValueOrDefault(existing ?? name) + 1;
        }
        observed.Add(new ObservedRow(
            slot,
            parsed.ArtifactName,
            parsed.State,
            parsed.CooldownSeconds,
            parsed.UsesRemaining,
            parsed.UsesTotal));
    }

    /// <summary>按关注名单过滤；名单为空时全部显示（否则界面上什么都看不到，没法用）。</summary>
    private void BuildRecognitionText(DateTimeOffset now, string engineLabel)
    {
        IReadOnlyList<TrackerEntryView> entries = _tracker.Snapshot(now);
        Dictionary<string, int> sortRanks = _watchlistConfig.SortOrder
            .Select((name, index) => (name, index))
            .ToDictionary(item => item.name, item => item.index, StringComparer.Ordinal);

        var lines = new List<string>();
        var shown = new List<TrackerEntryView>();

        // 先在必要时做名单过滤，并缓存每条的命中结果（供排序取名与展示取名复用）。
        var matches = new Dictionary<string, WatchlistMatch?>(StringComparer.Ordinal);
        var visible = new List<TrackerEntryView>(entries.Count);
        foreach (TrackerEntryView entry in entries)
        {
            WatchlistMatch? match = _watchlist.Match(entry.ArtifactName);
            if (!_watchlist.IsEmpty && match is null)
            {
                continue;
            }

            matches[EntryKey(entry)] = match;
            visible.Add(entry);
        }

        List<TrackerEntryView> visibleEntries = OverlaySort.Apply(
            visible,
            OverlaySort.Parse(_config.Overlay.SortMode),
            sortRanks,
            entry => matches.TryGetValue(EntryKey(entry), out WatchlistMatch? m) && m is not null
                ? m.CanonicalName
                : entry.ArtifactName);

        foreach (TrackerEntryView entry in visibleEntries)
        {
            WatchlistMatch? match = matches.TryGetValue(EntryKey(entry), out WatchlistMatch? hit) ? hit : null;

            shown.Add(entry);

            string name = match?.CanonicalName ?? entry.ArtifactName;
            string status = entry.State switch
            {
                ArtifactState.Ready => "[R]",
                ArtifactState.Cooling => $"[{entry.CooldownSeconds}]",
                _ => "[?]",
            };

            if (entry.UsesRemaining is { } remaining && entry.UsesTotal is { } total)
            {
                status += $"{remaining}/{total}";
            }

            string source = entry.Source == EntrySource.Live ? "实时" : "外推";
            lines.Add($"#{(int)entry.Page}-{entry.Slot:00}  {name} {status}   ({source})");
        }

        var sb = new StringBuilder();
        sb.Append("识别结果：");
        sb.Append(entries.Count).Append(" 条在跟踪");

        if (_watchlist.IsEmpty)
        {
            sb.Append("（名单为空 ⇒ 全部显示）");
        }
        else
        {
            sb.Append("，名单 ").Append(_watchlist.Entries.Count).Append(" 项");
        }

        sb.Append("    ").Append(engineLabel).Append(" ").Append(_lastOcrMs.ToString("0", CultureInfo.InvariantCulture)).Append(" ms");

        if (lines.Count == 0)
        {
            sb.AppendLine().Append("（暂无可显示的条目）");
        }
        else
        {
            foreach (string line in lines.Take(14))
            {
                sb.AppendLine().Append("  ").Append(line);
            }
        }

        string[] recentNames;
        lock (_recentNamesGate)
        {
            recentNames = CompactRecentNames().ToArray();
        }

        if (recentNames.Length > 0)
        {
            sb.AppendLine().Append("最近识别到的神器名：").Append(string.Join('、', recentNames));
        }

        _recognitionText = sb.ToString();

        // 叠加窗口是 UI 元素，必须回到 UI 线程更新。
        bool hasWatchlist = !_watchlist.IsEmpty;
        _dispatcher.BeginInvoke(() =>
        {
            _overlay?.UpdateEntries(shown, _config.Overlay, hasWatchlist);
            EnsureOverlayAvoidsRoi();
        });
    }

    private IReadOnlyList<string> CompactRecentNames()
    {
        return _recentNameCounts
            .Where(pair => pair.Value >= 2 || _recentNameCounts.Count <= 8)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(20)
            .Select(pair => pair.Key)
            .ToArray();
    }

    private static bool IsRecentAlias(string left, string right)
    {
        string a = Similar.Normalize(left);
        string b = Similar.Normalize(right);
        if (a == b) return true;
        if (a.Length < 3 || b.Length < 3) return false;
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)) return true;
        return Math.Min(a.Length, b.Length) >= 4 && Similar.Ratio(a, b) >= 0.82;
    }

    private void ObserveListStructure(RowReport report, ImageFrame frame)
    {
        _rowCount = report.RowCount;
        _rowPitch = report.Grid?.Pitch ?? report.MedianPitch;

        // 超过单页上限 ⇒ 量测不可信（ROI 混进了非列表内容）。
        // 这种观测绝不能喂给结构跟踪器，否则会把「基准行数」污染成错误量级。
        if (report.RowCount > ListRules.MaxRowsPerPage)
        {
            if (DateTime.UtcNow - _lastOverflowWarnAt >= FollowWarnMinInterval)
            {
                _lastOverflowWarnAt = DateTime.UtcNow;
                _log?.Warn($"[列表观测] 稳健行数 {report.RowCount} 超过单页上限 {ListRules.MaxRowsPerPage}，本次观测已丢弃。");
            }

            _previousFrame = frame;
            return;
        }

        // 签名只取**网格内**的行带：离群行带（场景物件、窗口边缘）会让签名随机抖动。
        string signature = ListStructureSnapshot.BuildSignature(report.GridBands);

        // 注意：必须在 Observe 之前取 HasBaseline——Observe 会建立基准，
        // 之后再判断就永远为真，初始观测会被静默吞掉。
        bool hadBaseline = _structureTracker.HasBaseline;
        ListChange? change = _structureTracker.Observe(
            new ListStructureSnapshot(report.RowCount, _rowPitch, signature));

        if (!hadBaseline)
        {
            _previousFrame = frame;
            _log?.Info(
                $"[列表观测] 初始：稳健行数={report.RowCount} 检出={report.Bands.Count} 离群={report.OutlierBands.Count} "
                + $"行距={report.Grid?.Pitch:0.##} 横向区域={report.RegionCount}");
            return;
        }

        if (change is null)
        {
            _previousFrame = frame;
            return;
        }

        string kind = change.Kind switch
        {
            ListChangeKind.RowCountChanged => $"行数 {change.PreviousRowCount} → {change.CurrentRowCount}",
            ListChangeKind.ContentChanged => "行内容变化",
            _ => $"行数与内容同时变化（{change.PreviousRowCount} → {change.CurrentRowCount}）",
        };

        if (change.LooksLikePageChange)
        {
            kind += $"（疑似翻页：显著少于基准行数 {_structureTracker.BaselineRowCount}）";
        }

        // 纯内容变化只做低频记录：翻页的主信号是**行数骤降**，
        // 而内容签名在场景/视频等非列表内容上会频繁抖动，全量落盘只会刷爆日志与磁盘。
        if (change.Kind == ListChangeKind.ContentChanged)
        {
            _previousFrame = frame;

            if (DateTime.UtcNow - _lastContentChangeLogAt >= ContentChangeLogMinInterval)
            {
                _lastContentChangeLogAt = DateTime.UtcNow;
                _log?.Info($"[列表观测] {kind}（行数不变 {change.CurrentRowCount}）；行距={report.Grid?.Pitch:0.##}");
            }

            return;
        }

        _log?.Info($"[列表观测] {kind}；行距={report.Grid?.Pitch:0.##}；签名={signature}");

        SaveChangePair(kind);
        _previousFrame = frame;
    }

    /// <summary>把变化前后两帧落盘，限流避免场景抖动把磁盘刷爆；容量由独立目录自己滚动管理。</summary>
    private void SaveChangePair(string kind)
    {
        if (DateTime.UtcNow - _lastChangeSaveAt < ChangeSaveMinInterval
            || _changeShots is null
            || _previousFrame is null
            || _lastFrame is null)
        {
            return;
        }

        try
        {
            _changeShots.Save(_previousFrame, "before");
            string saved = _changeShots.Save(_lastFrame, "after");
            _changeSaveCount++;
            _lastChangeSaveAt = DateTime.UtcNow;
            _notice = $"检测到列表变化（{kind}），已存前后两帧：{Path.GetFileName(saved)}";
        }
        catch (Exception ex)
        {
            _log?.Error("[列表观测] 保存变化帧失败", ex);
        }
    }

    private static string EntryKey(TrackerEntryView entry) => $"P{(int)entry.Page}#{entry.Slot}";

    /// <summary>识别刷新间隔，来自配置并钳制到安全范围（M5：刷新频率）。</summary>
    private TimeSpan CurrentOcrInterval()
        => TimeSpan.FromMilliseconds(Math.Clamp(_config.Recognition.IntervalMs, 200, 5000));

}
