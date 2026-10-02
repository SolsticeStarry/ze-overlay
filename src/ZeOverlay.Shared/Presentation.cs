namespace ZeOverlay.Shared;

/// <summary>S7 的输入：帧 + 待展示条目 + 状态文本。</summary>
public sealed record PresentationInput(
    ImageFrame Frame,
    IReadOnlyList<DisplayEntry> Entries,
    string Status,
    string EngineLabel,
    bool HasWatchlist);

/// <summary>S7 的输出：本次要显示的识别文本（供预览窗口复用）。</summary>
public sealed record PresentationResult(string RecognitionText);
