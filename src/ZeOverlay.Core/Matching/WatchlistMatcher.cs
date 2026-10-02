using ZeOverlay.Core.Tracking;

namespace ZeOverlay.Core.Matching;

/// <summary>一次名单命中。<see cref="CanonicalName"/> 是名单里的标准写法。</summary>
public sealed record WatchlistMatch(string CanonicalName, string ObservedName, double Score);

public sealed class WatchlistMatcherOptions
{
    /// <summary>
    /// 模糊匹配阈值，可调（PLAN 第 1.2 节「宽松度可调」）。
    ///
    /// 默认 **0.85** 而不是更松：实测阈值 0.6 时 `黑色瓶中闪电` 会误配到名单里的 `紫色瓶中闪电`
    /// （6 字名字只差首字 ≈ 0.83），把用户不关心的神器显示了出来。
    /// 而实测 OCR 对**神器名**的识别是准的（23 行样本里神器名全对，错字都出在玩家名和 `]`），
    /// 所以这里优先保证「不误报」。
    /// </summary>
    public double Threshold { get; set; } = 0.85;

    /// <summary>最佳候选必须比次佳好出这么多，否则视为不明确、不命中。</summary>
    public double Margin { get; set; } = 0.05;
}

/// <summary>
/// 关注名单匹配：**既是过滤器，也是纠错器**（`PLAN.md` 第 2.6 节）。
///
/// - 命中 ⇒ 返回名单里的**标准写法**，顺带纠正 OCR 错字；
/// - 未命中 ⇒ 名单外，调用方应完全隐藏；
/// - 名单为空 ⇒ 调用方应「全部显示」，否则界面上什么都看不到，无法工作。
/// </summary>
public sealed class WatchlistMatcher
{
    private readonly List<string> _entries;
    private readonly WatchlistMatcherOptions _options;

    public WatchlistMatcher(IEnumerable<string>? entries, WatchlistMatcherOptions? options = null)
    {
        _entries = (entries ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        _options = options ?? new WatchlistMatcherOptions();
    }

    public IReadOnlyList<string> Entries => _entries;

    public bool IsEmpty => _entries.Count == 0;

    public WatchlistMatch? Match(string? observedName)
    {
        if (string.IsNullOrWhiteSpace(observedName))
        {
            return null;
        }

        string key = TextSimilarity.Normalize(observedName);
        if (key.Length == 0)
        {
            return null;
        }

        WatchlistMatch? best = null;
        double secondBest = 0;

        foreach (string entry in _entries)
        {
            double score = TextSimilarity.Ratio(key, entry);
            if (score < _options.Threshold)
            {
                continue;
            }

            if (best is null || score > best.Score)
            {
                secondBest = best?.Score ?? 0;
                best = new WatchlistMatch(entry, observedName, score);
            }
            else if (score > secondBest)
            {
                secondBest = score;
            }
        }

        if (best is null)
        {
            return null;
        }

        // 两个名单项得分太接近 ⇒ 不明确，宁可当作未命中，也不显示错的东西。
        return best.Score - secondBest < _options.Margin ? null : best;
    }
}

