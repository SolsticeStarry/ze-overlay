using ZeOverlay.Shared;

namespace ZeOverlay.Stage.Recognition;

/// <summary>识别后端抽象：把 PP-OCR 与系统 OCR 统一成同一形状。</summary>
public interface IRowRecognizer
{
    string Name { get; }

    bool TryRecognizeRows(
        ImageFrame frame,
        IReadOnlyList<RowBand> bands,
        out IReadOnlyList<RecognizedRow> rows,
        out string? error);
}
