using ZeOverlay.Shared;

namespace ZeOverlay.Stage.Recognition;

// 注意：这里刻意不叫 OcrLine/OcrWord，避免与 Windows.Media.Ocr 的同名类型冲突。
public sealed record OcrTextWord(string Text, int X, int Y, int Width, int Height);

public sealed record OcrTextLine(
    string Text,
    int X,
    int Y,
    int Width,
    int Height,
    IReadOnlyList<OcrTextWord> Words);

/// <summary>识别后端抽象（与截屏后端同样的分层思路，便于替换）。</summary>
public interface IOcrEngine : IDisposable
{
    string Name { get; }

    bool IsAvailable { get; }

    bool TryRecognize(ImageFrame frame, out IReadOnlyList<OcrTextLine> lines, out string? error);
}
