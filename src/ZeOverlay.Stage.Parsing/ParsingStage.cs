using ZeOverlay.Shared;

namespace ZeOverlay.Stage.Parsing;

/// <summary>S4 实现：括号锚定解析，带上原槽位。</summary>
public sealed class ParsingStage : IParsingStage
{
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

            ParsedRow? parsed = Parser.Parse(row.Text);
            if (parsed is not null)
            {
                result.Add(parsed with { Slot = row.Slot });
            }
        }

        return result;
    }
}
