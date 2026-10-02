using ZeOverlay.Shared;

namespace ZeOverlay.Stage.Recognition;

/// <summary>S3 实现（系统 OCR）：行级后端，S3 的「行裁剪」路径不支持它。</summary>
public sealed class SystemOcrRecognitionStage : IRecognitionStage
{
    private readonly IOcrEngine _engine;

    public SystemOcrRecognitionStage(IOcrEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public string Name => _engine.Name;

    public IReadOnlyList<RecognizedRow> Process(IReadOnlyList<RowCrop> crops)
        => throw new NotSupportedException("系统 OCR 是行级后端；S3 的行裁剪路径只支持 PP-OCR。");
}
