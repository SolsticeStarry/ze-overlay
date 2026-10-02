using ZeOverlay.Shared;

namespace ZeOverlay.Stage.Recognition;

/// <summary>把系统 OCR 适配成统一的 <see cref="IRowRecognizer"/>（置信度记 1.0）。</summary>
public sealed class SystemOcrRecognizer : IRowRecognizer
{
    private readonly IOcrEngine _engine;

    public SystemOcrRecognizer(IOcrEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public string Name => _engine.Name;

    public bool TryRecognizeRows(
        ImageFrame frame,
        IReadOnlyList<RowBand> bands,
        out IReadOnlyList<RecognizedRow> rows,
        out string? error)
    {
        if (!_engine.TryRecognize(frame, out IReadOnlyList<OcrTextLine> lines, out error))
        {
            rows = [];
            return false;
        }

        var mapped = new List<RecognizedRow>(lines.Count);
        int slot = 0;

        foreach (OcrTextLine line in lines)
        {
            mapped.Add(new RecognizedRow(slot++, line.Text, 1.0));
        }

        rows = mapped;
        return true;
    }
}
