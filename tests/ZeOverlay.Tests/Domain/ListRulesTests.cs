
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

namespace ZeOverlay.Tests;

/// <summary>
/// 领域规则校验。常量全部来自真机确认（单页 ≤ 12 行、总数很少超过 15）。
/// 这些校验的价值在于：能挡掉「ROI 框进了非列表内容」这类错误，
/// 而不是等到识别阶段才发现行数对不上。
/// </summary>
public class ListRulesTests
{
    [Fact]
    public void FullPage_WithReasonableRoiHeight_IsClean()
    {
        // 12 行（满页）× 行距 30 → 需要约 400 px 高
        IReadOnlyList<string> warnings = ListRules.ValidateRoi(12, 30, 400);

        Assert.Empty(warnings);
    }

    [Fact]
    public void MoreRowsThanPageLimit_Warns()
    {
        // 单页上限 12；检出 13+ 说明混进了非列表内容
        IReadOnlyList<string> warnings = ListRules.ValidateRoi(13, 30, 400);

        Assert.Single(warnings);
        Assert.Contains("单页上限", warnings[0]);
        Assert.Contains("13", warnings[0]);
    }

    [Fact]
    public void OverlyTallRoi_WarnsToNarrow()
    {
        // 实测用户曾框到 535 px 高（单页只需 ~400），下方多出的部分会引入无关内容
        IReadOnlyList<string> warnings = ListRules.ValidateRoi(12, 30, 535);

        Assert.Single(warnings);
        Assert.Contains("下边界", warnings[0]);
    }

    [Fact]
    public void PartialPage_WithTightRoi_IsClean()
    {
        // 第 2 页通常只有 1–3 行
        Assert.Empty(ListRules.ValidateRoi(3, 30, 140));
    }

    [Fact]
    public void UnknownPitch_SkipsHeightCheck()
    {
        // 行数不足、定不出步长时不应因为身高而误报
        Assert.Empty(ListRules.ValidateRoi(2, null, 900));
    }

    [Fact]
    public void BothProblemsAtOnce_ReportsBoth()
    {
        IReadOnlyList<string> warnings = ListRules.ValidateRoi(20, 30, 900);

        Assert.Equal(2, warnings.Count);
    }
}
