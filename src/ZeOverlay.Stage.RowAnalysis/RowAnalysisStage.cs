using ZeOverlay.Shared;

namespace ZeOverlay.Stage.RowAnalysis;

/// <summary>S1 实现：行剖分 + 网格（含相同像素复用）。</summary>
public sealed class RowAnalysisStage : IRowAnalysisStage
{
    private readonly Cache _cache = new();

    public string Name => "行分析";

    public RowReport Process(ImageFrame frame) => _cache.Analyze(frame, f => Analyzer.Analyze(f));
}
