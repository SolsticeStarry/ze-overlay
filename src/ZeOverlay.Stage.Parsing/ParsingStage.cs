using ZeOverlay.Shared;

namespace ZeOverlay.Stage.Parsing;

/// <summary>
/// S4 实现：**逐行自动判断格式**（本服括号锚定 / 社区服 Plain），带上原槽位。
/// 语法不再需要人工选档案：<see cref="Parser.ParseAuto"/> 会按有无状态括号自行分流。
/// </summary>
public sealed class ParsingStage : IParsingStage
{
    private readonly IReadOnlyList<string> _vocabulary;

    /// <param name="mode">已废弃，仅为兼容旧调用方保留；行语法现在自动判断。</param>
    /// <param name="vocabulary">
    /// 神器名表（社区服 Plain 用）：用于切分名称，并把 OCR 读花的名称挡掉。为空则不做名称校验。
    /// </param>
    public ParsingStage(ParserMode mode = ParserMode.Bracket, IReadOnlyList<string>? vocabulary = null)
    {
        _vocabulary = vocabulary ?? [];
    }

    /// <summary>低于此置信度的行丢弃（PP-OCR 幻觉）；系统 OCR 记 1.0，不受影响。</summary>
    public double MinConfidence { get; set; } = 0.6;

    public string Name => "解析";

    public IReadOnlyList<ParsedRow> Process(IReadOnlyList<RecognizedRow> input)
    {
        var result = new List<ParsedRow>(input.Count);

        foreach (RecognizedRow row in input)
        {
            if (row.Confidence < MinConfidence)
            {
                continue;
            }

            ParsedRow? parsed = Parser.ParseAuto(row.Text, _vocabulary);
            if (parsed is not null)
            {
                result.Add(parsed with { Slot = row.Slot });
            }
        }

        return result;
    }
}
