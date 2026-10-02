namespace ZeOverlay.Shared;

/// <summary>一行识别结果：槽位 + 文本 + 置信度。</summary>
public sealed record RecognizedRow(int Slot, string Text, double Confidence);
