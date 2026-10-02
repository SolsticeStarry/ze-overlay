using ZeOverlay.Shared;

namespace ZeOverlay.Stage.Recognition;

/// <summary>S3 实现（PP-OCR rec）：对行裁剪批量识别。</summary>
public sealed class PpOcrRecognitionStage : IRecognitionStage
{
    private readonly PpOcrEngine _engine;
    private readonly int _fixedWidth;
    private readonly int _batchSize;

    public PpOcrRecognitionStage(PpOcrEngine engine, int fixedWidth = 0, int batchSize = 1)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _fixedWidth = fixedWidth;
        _batchSize = batchSize;
    }

    public string Name => _engine.Name;

    public IReadOnlyList<RecognizedRow> Process(IReadOnlyList<RowCrop> crops)
    {
        IReadOnlyList<PpOcrRow> source = _engine.RecognizeCrops(crops, _fixedWidth, _batchSize);
        var mapped = new List<RecognizedRow>(source.Count);
        foreach (PpOcrRow row in source)
        {
            mapped.Add(new RecognizedRow(row.Slot, row.Text, row.Confidence));
        }

        return mapped;
    }
}
