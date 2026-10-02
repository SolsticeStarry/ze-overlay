using ZeOverlay.Shared;
using ZeOverlay.Stage.GlyphSegmentation;
using ZeOverlay.Stage.Matching;
using ZeOverlay.Stage.Parsing;
using ZeOverlay.Stage.Recognition;
using ZeOverlay.Stage.RowAnalysis;
using ZeOverlay.Stage.Tracking;

namespace ZeOverlay.Infrastructure;

/// <summary>
/// 管线：只定义 S1→S6 的顺序，不含实现。
/// S0（采集）由宿主循环调用；S7（展示）由宿主在 UI 线程调用。
/// </summary>
public sealed class Pipeline
{
    private readonly IRowAnalysisStage _rowAnalysis;
    private readonly IGlyphSegmentationStage _glyphSegmentation;
    private readonly IRecognitionStage _recognition;
    private readonly IParsingStage _parsing;
    private readonly ITrackingStage _tracking;
    private readonly IMatchingStage _matching;

    public Pipeline(
        IRowAnalysisStage rowAnalysis,
        IGlyphSegmentationStage glyphSegmentation,
        IRecognitionStage recognition,
        IParsingStage parsing,
        ITrackingStage tracking,
        IMatchingStage matching)
    {
        _rowAnalysis = rowAnalysis;
        _glyphSegmentation = glyphSegmentation;
        _recognition = recognition;
        _parsing = parsing;
        _tracking = tracking;
        _matching = matching;
    }

    public PipelineStepResult Step(ImageFrame frame, DateTimeOffset now)
        => Step(frame, _rowAnalysis.Process(frame), now);

    /// <summary>用宿主已算好的行报告跑 S2–S6（避免重复行分析）。</summary>
    public PipelineStepResult Step(ImageFrame frame, RowReport report, DateTimeOffset now)
    {
        IReadOnlyList<RowCrop> crops = _glyphSegmentation.Process(new RowInput(frame, report));
        IReadOnlyList<RecognizedRow> recognized = _recognition.Process(crops);
        IReadOnlyList<ParsedRow> parsed = _parsing.Process(recognized);
        TrackingResult tracking = _tracking.Process(new TrackingInput(report.RowCount, parsed, now));
        IReadOnlyList<DisplayEntry> entries = _matching.Process(tracking);
        return new PipelineStepResult(report, entries);
    }
}
