namespace ZeOverlay.Shared;

/// <summary>一个档案在某一帧上的自动选档得分。</summary>
public sealed record ProfileScore(string Id, int NameHits);

/// <summary>
/// 自动选档：按「识别到的神器名命中该档案名称表的条数」选择服务器档案。
///
/// 纯逻辑，便于单测；宿主负责采集各档案 ROI、做识别/解析并计数，
/// 再按这里的结果做**滞回**切换（同一胜者连续出现才切）。
/// </summary>
public static class ProfileDetector
{
    /// <summary>胜出档案至少要有这么多条名字命中，否则认为只是游戏场景噪声。</summary>
    public const int MinSwitchHits = 2;

    /// <summary>胜出档案需要比当前档案多命中这么多条，才值得切换。</summary>
    public const int Margin = 1;

    /// <summary>
    /// 返回应当切换到的档案 Id；不应切换（当前已是最优或优势不足）时返回 null。
    /// 相等时不切换，避免抖动。
    /// </summary>
    public static string? Choose(string activeId, IReadOnlyList<ProfileScore> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);

        if (scores.Count == 0)
        {
            return null;
        }

        ProfileScore best = scores[0];
        int activeHits = 0;

        foreach (ProfileScore score in scores)
        {
            if (string.Equals(score.Id, activeId, StringComparison.Ordinal))
            {
                activeHits = score.NameHits;
            }

            if (score.NameHits > best.NameHits
                || (score.NameHits == best.NameHits && string.CompareOrdinal(score.Id, best.Id) < 0))
            {
                best = score;
            }
        }

        if (string.Equals(best.Id, activeId, StringComparison.Ordinal))
        {
            return null;
        }

        if (best.NameHits < MinSwitchHits || best.NameHits - activeHits < Margin)
        {
            return null;
        }

        return best.Id;
    }
}
