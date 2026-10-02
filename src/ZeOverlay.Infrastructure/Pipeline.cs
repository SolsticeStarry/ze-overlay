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

    /// <summary>
    /// 只做 S2+S3（字形切分 → 识别），不做解析/跟踪/匹配。
    /// 供「自动选档」对任意档案的 ROI 抓帧后复用同一条识别链路打分。
    /// </summary>
    public IReadOnlyList<RecognizedRow> RecognizeRows(ImageFrame frame, RowReport report)
        => _recognition.Process(_glyphSegmentation.Process(new RowInput(frame, report)));

    /// <summary>用宿主已算好的行报告跑 S2–S6（避免重复行分析）。</summary>
    public PipelineStepResult Step(ImageFrame frame, RowReport report, DateTimeOffset now)
    {
        IReadOnlyList<RowCrop> crops = _glyphSegmentation.Process(new RowInput(frame, report));
        IReadOnlyList<RecognizedRow> recognized = _recognition.Process(crops);
        return StepFromRecognized(frame, report, recognized, now);
    }

    /// <summary>
    /// 用**已经识别好的行**跑 S4–S6（解析 → 跟踪 → 匹配）。
    /// 自动选档会先调 <see cref="RecognizeRows"/> 为各档案打分，胜出档案的行可直接从这里进管线，避免重复识别。
    /// </summary>
    public PipelineStepResult StepFromRecognized(
        ImageFrame frame,
        RowReport report,
        IReadOnlyList<RecognizedRow> recognized,
        DateTimeOffset now)
    {
        IReadOnlyList<ParsedRow> parsed = _parsing.Process(recognized);
        TrackingResult tracking = _tracking.Process(new TrackingInput(report.RowCount, parsed, now));
        IReadOnlyList<DisplayEntry> entries = _matching.Process(tracking);
        return new PipelineStepResult(report, entries);
    }
}
