using ZeOverlay.Shared;
using ZeOverlay.Stage.Parsing;

namespace ZeOverlay.Tests.Parsing;

/// <summary>
/// 社区服（Plain 模式）行解析回归。输入全部是 <c>ZeOverlay.Cli --recognize --ppocr</c>
/// 在两张真机截图上的**原始 OCR 文本**（`docs/REAL_SAMPLES.md` 样本 #5 / #6），
/// 因此这里锁定的就是真机事实，而不是构造的理想字符串。
/// </summary>
public sealed class PlainLineParserTests
{
    [Theory]
    // 样本 #5（屏幕截图(2056)）
    [InlineData("人闪1绿光投影仪19s", "人闪", "绿光投影仪", ArtifactState.Cooling, 19, null, null)]
    [InlineData("口粮1菠萝霸霸1/1", "口粮", "菠萝霸霸", ArtifactState.Ready, null, 1, 1)]
    [InlineData("大火盐1纯爱薄纱牛头..42s", "大火盐", "纯爱薄纱牛头", ArtifactState.Cooling, 42, null, null)]
    [InlineData("大火盐2Evi就绪", "大火盐", "Evi", ArtifactState.Ready, null, null, null)]
    [InlineData("大火盐3米浴浴口V就绪", "大火盐", "米浴浴口V", ArtifactState.Ready, null, null, null)]
    [InlineData("大荧光1漆黑默罗兰5/5", "大荧光", "漆黑默罗兰", ArtifactState.Ready, null, 5, 5)]
    [InlineData("定位2夫玖的蓝色16s", "定位", "夫玖的蓝色", ArtifactState.Cooling, 16, null, null)]
    [InlineData("手电1呆头呆脑很迷糊0", "手电", "呆头呆脑很迷糊", ArtifactState.Ready, null, null, null)]
    [InlineData("手电2小细节大进步0", "手电", "小细节大进步", ArtifactState.Ready, null, null, null)]
    [InlineData("桶装水1高大英俊的老..46s", "桶装水", "高大英俊的老", ArtifactState.Cooling, 46, null, null)]
    [InlineData("水枪1霓光Niko69s", "水枪", "霓光Niko", ArtifactState.Cooling, 69, null, null)]
    [InlineData("水枪2居夫优势图就绪", "水枪", "居夫优势图", ArtifactState.Ready, null, null, null)]
    [InlineData("黑闪1老默1/1", "黑闪", "老默", ArtifactState.Ready, null, 1, 1)]
    // 样本 #6（屏幕截图(2057)）
    [InlineData("人闪1大头吧唧44s", "人闪", "大头吧唧", ArtifactState.Cooling, 44, null, null)]
    [InlineData("大火盐1霓光Niko就绪", "大火盐", "霓光Niko", ArtifactState.Ready, null, null, null)]
    [InlineData("定位1士享文14s", "定位", "士享文", ArtifactState.Cooling, 14, null, null)]
    [InlineData("定位2雨露之夏1.5s", "定位", "雨露之夏", ArtifactState.Cooling, 1, null, null)] // 小数丢弃，只取整数
    [InlineData("定位3知意雨25s", "定位", "知意雨", ArtifactState.Cooling, 25, null, null)]
    [InlineData("手电1请叫我熊8", "手电", "请叫我熊", ArtifactState.Ready, null, null, null)]
    [InlineData("手电2宇宙机器人8", "手电", "宇宙机器人", ArtifactState.Ready, null, null, null)]
    [InlineData("手电3苦涩的柠檬8", "手电", "苦涩的柠檬", ArtifactState.Ready, null, null, null)]
    [InlineData("水枪1无名字的好人就绪", "水枪", "无名字的好人", ArtifactState.Ready, null, null, null)]
    [InlineData("爆闪1屠夫优势图49s", "爆闪", "屠夫优势图", ArtifactState.Cooling, 49, null, null)]
    [InlineData("爆闪2绿光投影仪就绪", "爆闪", "绿光投影仪", ArtifactState.Ready, null, null, null)]
    [InlineData("黑闪1花雲の魔女1/1", "黑闪", "花雲の魔女", ArtifactState.Ready, null, 1, 1)]
    public void ParsePlain_RealOcrLines(
        string raw,
        string expectedName,
        string expectedPlayer,
        ArtifactState expectedState,
        int? expectedCooldown,
        int? expectedRemaining,
        int? expectedTotal)
    {
        ParsedRow? parsed = Parser.Parse(raw, ParserMode.Plain);

        Assert.NotNull(parsed);
        Assert.Equal(expectedName, parsed!.ArtifactName);
        Assert.Equal(expectedPlayer, parsed.PlayerName);
        Assert.Equal(expectedState, parsed.State);
        Assert.Equal(expectedCooldown, parsed.CooldownSeconds);
        Assert.Equal(expectedRemaining, parsed.UsesRemaining);
        Assert.Equal(expectedTotal, parsed.UsesTotal);
        Assert.Equal(raw, parsed.RawText);
    }

    [Theory]
    [InlineData("#大火盐1纯爱薄纱牛头..42s", "大火盐", "纯爱薄纱牛头")]
    [InlineData("@水枪1霓光Niko69s", "水枪", "霓光Niko")]
    public void ParsePlain_StripsLeadingMarkerNoise(string raw, string expectedName, string expectedPlayer)
    {
        ParsedRow? parsed = Parser.Parse(raw, ParserMode.Plain);

        Assert.NotNull(parsed);
        Assert.Equal(expectedName, parsed!.ArtifactName);
        Assert.Equal(expectedPlayer, parsed.PlayerName);
    }

    [Theory]
    [InlineData("地图神器·人类")] // 列表标题：没有状态，必须丢弃，否则变成幽灵条目
    [InlineData("REC")]
    [InlineData("SlotMode")]
    public void ParsePlain_DropsRowsWithoutStatus(string raw)
        => Assert.Null(Parser.Parse(raw, ParserMode.Plain));

    [Theory]
    [InlineData("大火盐1纯爱薄纱牛头49")] // 冷却 `49s` 丢了结尾 s：**不能**被当成就绪（否则长倒计时跳 [R]）
    [InlineData("定位1士享爻27")]
    [InlineData("水枪1霓光Niko13")]
    public void ParsePlain_DroppedCooldownSuffix_IsNotTreatedAsReady(string raw)
        => Assert.Null(Parser.Parse(raw, ParserMode.Plain));

    [Theory]
    [InlineData("水枪无名字的好人就绪")] // 有状态但没标号、又没给名表：名称不可信
    [InlineData("手电呆头呆脑很迷糊∞")]
    public void ParsePlain_DropsRowsWithoutServerIndex(string raw)
        => Assert.Null(Parser.Parse(raw, ParserMode.Plain));

    [Theory]
    [InlineData("黑闪16花昙の魔女1/1")]   // 标号被读花成 16 ⇒ 离谱，丢弃（否则与 黑闪1 重复）
    [InlineData("定位310知意雨25s")]
    public void ParsePlain_DropsAbsurdServerIndex(string raw)
        => Assert.Null(Parser.Parse(raw, ParserMode.Plain));

    [Theory]
    [InlineData("手电王梦虎8", "手电", "王梦虎")]      // 标号被读丢，用名表最长前缀切
    [InlineData("桶装水Evi就绪", "桶装水", "Evi")]
    [InlineData("手电士章女8", "手电", "士章女")]
    public void ParsePlain_NoIndex_SplitsByVocabulary(string raw, string expectedName, string expectedPlayer)
    {
        ParsedRow? parsed = Parser.Parse(raw, ParserMode.Plain, ["手电", "桶装水", "大火盐", "爆闪"]);

        Assert.NotNull(parsed);
        Assert.Equal(expectedName, parsed!.ArtifactName);
        Assert.Equal(expectedPlayer, parsed.PlayerName);
        Assert.Null(parsed.ServerIndex);
    }

    [Fact]
    public void ParsePlain_StripsWhitespaceBetweenColumns()
    {
        // 截图里 `n / m` 与列之间有空隙，OCR 有时会带上空白；解析前统一去掉。
        ParsedRow? parsed = Parser.Parse(" 黑闪1  老默  1 / 1 ", ParserMode.Plain);

        Assert.NotNull(parsed);
        Assert.Equal("黑闪", parsed!.ArtifactName);
        Assert.Equal("老默", parsed.PlayerName);
        Assert.Equal(1, parsed.UsesRemaining);
        Assert.Equal(1, parsed.UsesTotal);
    }

    [Fact]
    public void ParsePlain_InfinitySymbol_MapsToReady()
    {
        ParsedRow? parsed = Parser.Parse("手电1请叫我熊∞", ParserMode.Plain);

        Assert.NotNull(parsed);
        Assert.Equal("手电", parsed!.ArtifactName);
        Assert.Equal("请叫我熊", parsed.PlayerName);
        Assert.Equal(ArtifactState.Ready, parsed.State);
    }

    [Theory]
    [InlineData("滋水枪[R]亦陌雕", "滋水枪", "亦陌雕", ArtifactState.Ready)]   // 有状态括号 → 本服
    [InlineData("人闪1绿光投影仪19s", "人闪", "绿光投影仪", ArtifactState.Cooling)] // 无括号 → 社区服
    [InlineData("手电筒[R]烧烤鱼", "手电筒", "烧烤鱼", ArtifactState.Ready)]
    public void ParseAuto_DetectsFormatPerLine(string raw, string expectedName, string expectedPlayer, ArtifactState expectedState)
    {
        ParsedRow? parsed = Parser.ParseAuto(raw, ["人闪", "手电", "大火盐", "滋水枪", "手电筒"]);

        Assert.NotNull(parsed);
        Assert.Equal(expectedName, parsed!.ArtifactName);
        Assert.Equal(expectedPlayer, parsed.PlayerName);
        Assert.Equal(expectedState, parsed.State);
    }

    [Fact]
    public void ParsePlain_DefaultModeIsBracket_SoPlainTextIsNotMisparsed()
    {
        // 默认仍是本服语法：同一行用 Bracket 解析时不应被当成 Plain。
        ParsedRow? bracket = Parser.Parse("人闪1绿光投影仪19s");
        ParsedRow? plain = Parser.Parse("人闪1绿光投影仪19s", ParserMode.Plain);

        Assert.NotNull(plain);
        Assert.Equal("人闪", plain!.ArtifactName);
        Assert.Equal("绿光投影仪", plain.PlayerName);

        // Bracket 模式对本服文本的退化行为不是本测试重点，只确认两种模式确实分叉。
        Assert.True(bracket is null || bracket.ArtifactName != plain.ArtifactName
            || bracket.PlayerName != plain.PlayerName);
    }
}
