using ZeOverlay.Shared;
using ZeOverlay.Stage.Parsing;

namespace ZeOverlay.Tests.Parsing;

/// <summary>
/// Plain 模式：名称必须命中档案名表，否则视为 OCR 读花并丢弃——
/// 否则同一标号会因读花的名字建立出第二条重复条目（真机踩过）。
/// </summary>
public sealed class ParsingStageVocabularyFilterTests
{
    private static readonly string[] Vocabulary =
        ["人闪", "口粮", "大火盐", "大荧光", "定位", "手电", "桶装水", "水枪", "黑闪", "爆闪"];

    private static ParsingStage Stage() => new(ParserMode.Plain, Vocabulary);

    [Fact]
    public void DropsMisreadName()
    {
        var input = new List<RecognizedRow>
        {
            new(0, "优火盐1霓光Niko就绪", 0.9),  // `大火盐` 读成 `优火盐`
            new(1, "正位2雨露之夏14s", 0.9),      // `定位` 读成 `正位`
            new(2, "爆肉1屠夫优势图49s", 0.9),    // `爆闪` 读成 `爆肉`
        };

        Assert.Empty(Stage().Process(input));
    }

    [Fact]
    public void KeepsNamesInVocabulary()
    {
        var input = new List<RecognizedRow>
        {
            new(0, "大火盐1霓光Niko就绪", 0.9),
            new(1, "定位2雨露之夏14s", 0.9),
            new(2, "爆闪1屠夫优势图49s", 0.9),
        };

        IReadOnlyList<ParsedRow> parsed = Stage().Process(input);

        Assert.Equal(3, parsed.Count);
        Assert.Equal(new[] { "大火盐", "定位", "爆闪" }, parsed.Select(p => p.ArtifactName).ToArray());
    }

    [Fact]
    public void WithoutVocabulary_NoFiltering()
    {
        // 名表为空 ⇒ 不校验（与「名单为空 = 全部显示」一致），行仍保留。
        var stage = new ParsingStage(ParserMode.Plain);

        IReadOnlyList<ParsedRow> parsed = stage.Process([new RecognizedRow(0, "优火盐1霓光Niko就绪", 0.9)]);

        Assert.Single(parsed);
    }
}
