namespace ZeOverlay.Shared;

/// <summary>一行神器列表解析结果。</summary>
public sealed record ParsedRow(
    string ArtifactName,
    string PlayerName,
    ArtifactState State,
    int? CooldownSeconds,
    int? UsesRemaining,
    int? UsesTotal,
    string RawText,
    int Slot = 0);
