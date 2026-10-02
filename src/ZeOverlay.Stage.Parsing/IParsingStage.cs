using ZeOverlay.Shared;

namespace ZeOverlay.Stage.Parsing;

/// <summary>S4 契约：识别行 → 解析行。</summary>
public interface IParsingStage : IStage<IReadOnlyList<RecognizedRow>, IReadOnlyList<ParsedRow>>
{
}
