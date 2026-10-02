namespace ZeOverlay.Shared;

/// <summary>
/// 神器列表的领域规则。**全部来自真机确认**（见 `docs/REAL_SAMPLES.md`），不是猜的。
/// 这些常量被视觉层用来做有效性校验——它们能挡掉「ROI 框进了非列表内容」这类错误。
/// </summary>
public static class ListRules
{
    /// <summary>单页最多 12 行；超出的神器进入第 2 页。（真机确认）</summary>
    public const int MaxRowsPerPage = 12;

    /// <summary>一局里神器总数通常很少超过 15 ⇒ 第 2 页通常只有 1–3 行。（真机确认）</summary>
    public const int TypicalMaxTotal = 15;

    /// <summary>ROI 高度超过「单页所需」这么多倍时，提示可以收窄（避免把下方场景/其它 HUD 框进来）。</summary>
    private const double RoiHeightSlack = 1.25;

    private const int RoiHeightPadding = 40;

    /// <summary>
    /// 对一次 ROI 量测结果做领域校验，返回人类可读的告警；无问题时返回空列表。
    /// </summary>
    public static IReadOnlyList<string> ValidateRoi(int rowCount, double? pitch, int roiHeight)
    {
        var warnings = new List<string>();

        if (rowCount > MaxRowsPerPage)
        {
            warnings.Add(
                $"检出 {rowCount} 行，超过单页上限 {MaxRowsPerPage} 行 —— ROI 很可能把列表以外的内容也框了进来。");
        }

        if (pitch is > 0)
        {
            double needed = MaxRowsPerPage * pitch.Value + RoiHeightPadding;
            if (roiHeight > needed * RoiHeightSlack)
            {
                warnings.Add(
                    $"ROI 高度 {roiHeight}px 明显大于单页上限所需（≈{needed:0}px = {MaxRowsPerPage} 行 × 行距 {pitch:0.#}），"
                    + "可以把下边界往上收，减少无关内容。");
            }
        }

        return warnings;
    }
}
