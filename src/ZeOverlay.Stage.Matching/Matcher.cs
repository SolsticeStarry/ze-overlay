
using ZeOverlay.Shared;

namespace ZeOverlay.Stage.Matching;

/// <summary>
/// 关注名单匹配：**既是过滤器，也是纠错器**（`PLAN.md` 第 2.6 节）。
///
/// - 命中 ⇒ 返回名单里的**标准写法**，顺带纠正 OCR 错字；
/// - 未命中 ⇒ 名单外，调用方应完全隐藏；
/// - 名单为空 ⇒ 调用方应「全部显示」，否则界面上什么都看不到，无法工作。
/// </summary>
public sealed class Matcher
{
    private readonly List<string> _entries;
    private readonly WatchlistOptions _options;

    public Matcher(IEnumerable<string>? entries, WatchlistOptions? options = null)
    {
        _entries = (entries ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        _options = options ?? new WatchlistOptions();
    }

    public IReadOnlyList<string> Entries => _entries;

    public bool IsEmpty => _entries.Count == 0;

    public WatchlistMatch? Match(string? observedName)
    {
        if (string.IsNullOrWhiteSpace(observedName))
        {
            return null;
        }

        string key = Similar.Normalize(observedName);
        if (key.Length == 0)
        {
            return null;
        }

        WatchlistMatch? best = null;
        double secondBest = 0;

        foreach (string entry in _entries)
        {
            double score = Similar.Ratio(key, entry);
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

