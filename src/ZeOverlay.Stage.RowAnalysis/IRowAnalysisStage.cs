using ZeOverlay.Shared;

namespace ZeOverlay.Stage.RowAnalysis;

/// <summary>S1 契约：帧 → 行分析报告。</summary>
public interface IRowAnalysisStage : IStage<ImageFrame, RowReport>
{
}
