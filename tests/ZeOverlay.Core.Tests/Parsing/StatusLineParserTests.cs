using ZeOverlay.Core.Parsing;
using ZeOverlay.Core.Tracking;

namespace ZeOverlay.Core.Tests.Parsing;

/// <summary>
/// 行解析。测试语料**全部是真实 OCR 输出**（`docs/ref/panel_02_human.ocr.txt`），
/// 因此覆盖了实际会遇到的错字：`]`→`I`、全角括号、字间插入空格等。
/// </summary>
public class StatusLineParserTests
{
    // ---------- 真实 OCR 输出 ----------

    [Theory]
    [InlineData("滋 水 枪 [RI 范 德 彪 ． 奇 妙 冒 险", "滋水枪", "范德彪．奇妙冒险")]
    [InlineData("滋 水 枪 [RI 用 户 6116041", "滋水枪", "用户6116041")]
    [InlineData("紫 色 瓶 中 闪 电 [R] ±Komorebi", "紫色瓶中闪电", "±Komorebi")]
    [InlineData("袋 装 火 盐 [R] 析 构 万 理 的 发 条 公 主", "袋装火盐", "析构万理的发条公主")]
    [InlineData("手 电 筒 [RI 烧 烤 鱼", "手电筒", "烧烤鱼")]
    [InlineData("桶 装 杏 仁 水 [RI 心 形 坠 盒", "桶装杏仁水", "心形坠盒")]
    [InlineData("滋 水 枪 [RI 真 就 一 颗 a", "滋水枪", "真就一颗a")]
    [InlineData("袋 装 火 盐 [R] 用 户 6744311", "袋装火盐", "用户6744311")]
    [InlineData("手 电 筒 [R] 宝 宝 害 怕", "手电筒", "宝宝害怕")]
    [InlineData("紫 色 瓶 中 闪 电 [R] 心 绘 灵", "紫色瓶中闪电", "心绘灵")]
    public void ParsesReadyRowsFromRealOcrOutput(string ocrText, string expectedName, string expectedPlayer)
    {
        ParsedRow? row = StatusLineParser.Parse(ocrText);

        Assert.NotNull(row);
        Assert.Equal(expectedName, row.ArtifactName);
        Assert.Equal(expectedPlayer, row.PlayerName);
        Assert.Equal(ArtifactState.Ready, row.State);
        Assert.Null(row.CooldownSeconds);
    }

    [Theory]
    [InlineData("荧 光 棒 [ R ] 4 / 5 皇 家 口 粮", "荧光棒", "皇家口粮", 4, 5)]
    [InlineData("荧 光 棒 [ R ] 5 / 5 〖 猫 娘 〗 繁 花 凡", "荧光棒", "〖猫娘〗繁花凡", 5, 5)]
    public void ParsesUsesToken(string ocrText, string expectedName, string expectedPlayer, int remaining, int total)
    {
        ParsedRow? row = StatusLineParser.Parse(ocrText);

        Assert.NotNull(row);
        Assert.Equal(expectedName, row.ArtifactName);
        Assert.Equal(expectedPlayer, row.PlayerName);
        Assert.Equal(remaining, row.UsesRemaining);
        Assert.Equal(total, row.UsesTotal);
    }

    // ---------- 冷却（样本 #1 的实际行，OCR 未捕获冷却，用原文验证解析） ----------

    [Theory]
    [InlineData("爆闪相机 [12] 真就一颗a", "爆闪相机", 12, "真就一颗a")]
    [InlineData("灵体定位仪 [20] 小柒Lucky", "灵体定位仪", 20, "小柒Lucky")]
    [InlineData("爆闪相机[12]真就一颗a", "爆闪相机", 12, "真就一颗a")]
    public void ParsesCoolingRows(string text, string expectedName, int expectedSeconds, string expectedPlayer)
    {
        ParsedRow? row = StatusLineParser.Parse(text);

        Assert.NotNull(row);
        Assert.Equal(expectedName, row.ArtifactName);
        Assert.Equal(ArtifactState.Cooling, row.State);
        Assert.Equal(expectedSeconds, row.CooldownSeconds);
        Assert.Equal(expectedPlayer, row.PlayerName);
    }

    [Theory]
    [InlineData("皇家口粮 [R]1/1 亦陌雕", "皇家口粮", "亦陌雕", 1, 1)]
    [InlineData("黑色瓶中闪电 [R]1/1 不会狙娱乐", "黑色瓶中闪电", "不会狙娱乐", 1, 1)]
    public void ParsesReadyWithUses(string text, string name, string player, int remaining, int total)
    {
        ParsedRow? row = StatusLineParser.Parse(text);

        Assert.NotNull(row);
        Assert.Equal(name, row.ArtifactName);
        Assert.Equal(player, row.PlayerName);
        Assert.Equal(ArtifactState.Ready, row.State);
        Assert.Equal(remaining, row.UsesRemaining);
        Assert.Equal(total, row.UsesTotal);
    }

    // ---------- 边界 ----------

    [Fact]
    public void NameContainingBrackets_UsesLastMatchingBracketAsStatus()
    {
        // 名称里带方括号时，必须锚定**最后一个**符合「内容为 R 或数字」的括号
        ParsedRow? row = StatusLineParser.Parse("[测试]神器 [R] 玩家甲");

        Assert.NotNull(row);
        Assert.Equal("[测试]神器", row.ArtifactName);
        Assert.Equal("玩家甲", row.PlayerName);
        Assert.Equal(ArtifactState.Ready, row.State);
    }

    [Fact]
    public void PlayerNameStartingWithLatinI_IsNotEatenAsBracketMisread()
    {
        // `]` 后面紧跟以 I 开头的拉丁玩家名时，不能把 I 当成闭合括号吃掉
        ParsedRow? row = StatusLineParser.Parse("滋水枪 [R]IamPlayer");

        Assert.NotNull(row);
        Assert.Equal("滋水枪", row.ArtifactName);
        Assert.Equal("IamPlayer", row.PlayerName);
    }

    [Fact]
    public void DroppedClosingBracketBeforeLatinName_IsRecovered()
    {
        // 实测：OCR 把 `[R]` 读成 `[RI`，紧跟拉丁玩家名（`IMr.YYX` 的 I 其实是 `]`）
        ParsedRow? row = StatusLineParser.Parse("滋水枪 [RI Mr.YYYX");

        Assert.NotNull(row);
        Assert.Equal("滋水枪", row.ArtifactName);
        Assert.Equal("Mr.YYYX", row.PlayerName);
        Assert.Equal(ArtifactState.Ready, row.State);
    }

    [Fact]
    public void FullWidthDigits_AreParsed()
    {
        ParsedRow? row = StatusLineParser.Parse("滋水枪 [１２] 玩家甲");

        Assert.NotNull(row);
        Assert.Equal(ArtifactState.Cooling, row.State);
        Assert.Equal(12, row.CooldownSeconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("这是一行没有状态标记的文字")]
    [InlineData("[R] 只有状态没有名称")]
    [InlineData("[XY] 括号里不是 R 也不是数字")]
    public void InvalidLines_ReturnNull(string text)
    {
        Assert.Null(StatusLineParser.Parse(text));
    }

    // ---------- 括号形近字（真实 OCR 会把 [ ] 读成别的字符） ----------

    [Theory]
    [InlineData("滋水枪 ［R］ 玩家甲")]   // 全角方括号
    [InlineData("滋水枪 《R] 玩家甲")]   // 实测：《 出现在真实 OCR 输出里
    [InlineData("滋水枪 【R】 玩家甲")]
    [InlineData("滋水枪 （R） 玩家甲")]
    public void BracketLookalikes_AreAccepted(string text)
    {
        ParsedRow? row = StatusLineParser.Parse(text);

        Assert.NotNull(row);
        Assert.Equal("滋水枪", row.ArtifactName);
        Assert.Equal("玩家甲", row.PlayerName);
        Assert.Equal(ArtifactState.Ready, row.State);
    }

    [Fact]
    public void PlayerNameWithFullWidthBrackets_IsNotMistakenForStatus()
    {
        // 【猫娘】里的内容不是 R 也不是数字 ⇒ 不能被当成状态括号
        ParsedRow? row = StatusLineParser.Parse("荧光棒 [R]5/5 【猫娘】繁花凡");

        Assert.NotNull(row);
        Assert.Equal("荧光棒", row.ArtifactName);
        Assert.Equal("【猫娘】繁花凡", row.PlayerName);
        Assert.Equal(5, row.UsesRemaining);
    }

    // ---------- 兜底：方括号被整段丢掉 ----------

    [Theory]
    [InlineData("手电筒R烧烤鱼", "手电筒", "烧烤鱼")]
    [InlineData("手电筒R宝宝害怕", "手电筒", "宝宝害怕")]
    [InlineData("滋水枪r玩家甲", "滋水枪", "玩家甲")]
    public void DroppedBrackets_FallBackToBareReadyToken(string text, string name, string player)
    {
        ParsedRow? row = StatusLineParser.Parse(text);

        Assert.NotNull(row);
        Assert.Equal(name, row.ArtifactName);
        Assert.Equal(player, row.PlayerName);
        Assert.Equal(ArtifactState.Ready, row.State);
    }

    [Fact]
    public void DroppedBrackets_FallBackToBareCooldown()
    {
        ParsedRow? row = StatusLineParser.Parse("爆闪相机12真就一颗a");

        Assert.NotNull(row);
        Assert.Equal("爆闪相机", row.ArtifactName);
        Assert.Equal(ArtifactState.Cooling, row.State);
        Assert.Equal(12, row.CooldownSeconds);
        Assert.Equal("真就一颗a", row.PlayerName);
    }

    [Fact]
    public void LongDigitRunInPlayerName_IsNotMistakenForStatus()
    {
        // 玩家名里的长数字不能被当成冷却
        Assert.Null(StatusLineParser.Parse("滋水枪用户6744311"));
    }

    [Fact]
    public void UsesToken_IsNotMistakenForBareStatus()
    {
        // `4/5` 的 4 后面跟着 `/` ⇒ 是 n/m，不是状态；且整行没有状态 ⇒ 视为无法解析
        Assert.Null(StatusLineParser.Parse("滋水枪4/5玩家甲"));
    }

    [Fact]
    public void BareFallback_DoesNotRunWhenBracketsWerePresent()
    {
        // 括号存在但内容非法时，不应再从名称/玩家名里抠裸标记
        ParsedRow? row = StatusLineParser.Parse("R武器[R]玩家甲");

        Assert.NotNull(row);
        Assert.Equal("R武器", row.ArtifactName);   // 名称里的 R 没被误当状态
        Assert.Equal("玩家甲", row.PlayerName);
    }

    [Fact]
    public void DuplicateClosingBracket_IsFullyConsumed()
    {
        // 实测 PP-OCR 会吐出 `手电筒[R]】烧烤鱼` 这种重复闭括号
        ParsedRow? row = StatusLineParser.Parse("手电筒[R]】烧烤鱼");

        Assert.NotNull(row);
        Assert.Equal("手电筒", row.ArtifactName);
        Assert.Equal("烧烤鱼", row.PlayerName);
        Assert.Equal(ArtifactState.Ready, row.State);
    }

    [Theory]
    [InlineData("手电筒 [R]|宝宝害怕", "手电筒", "宝宝害怕")]
    [InlineData("滋水枪 [R]】用户6116041", "滋水枪", "用户6116041")]
    public void LeadingOrTrailingBracketNoise_IsTrimmed(string text, string name, string player)
    {
        ParsedRow? row = StatusLineParser.Parse(text);

        Assert.NotNull(row);
        Assert.Equal(name, row.ArtifactName);
        Assert.Equal(player, row.PlayerName);
    }

    [Fact]
    public void LegitimateFullWidthBracketsInPlayerName_AreKept()
    {
        // 收紧后的规则不能伤到合法的 `【猫娘】繁花凡`
        ParsedRow? row = StatusLineParser.Parse("荧光棒 [R]5/5 【猫娘】繁花凡");

        Assert.NotNull(row);
        Assert.Equal("【猫娘】繁花凡", row.PlayerName);
    }

    [Fact]
    public void KeepsRawTextForDebugging()
    {
        const string raw = "滋 水 枪 [RI 玩 家";

        ParsedRow? row = StatusLineParser.Parse(raw);

        Assert.NotNull(row);
        Assert.Equal(raw, row.RawText);
    }
}
