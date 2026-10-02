namespace ZeOverlay.Shared;

/// <summary>
/// 行解析语法。本服与各社区服的神器列表行格式不同，按档案选择。
/// </summary>
public enum ParserMode
{
    /// <summary>
    /// 本服：`名称 [R]/[数字] 可选n/m 玩家名`，状态在方括号里、且不在固定列，需要括号锚定。
    /// </summary>
    Bracket = 0,

    /// <summary>
    /// 社区服（entWatch 风格）：`神器简称+序号 玩家名 状态`，**无方括号、无分隔符**，
    /// 状态用 `就绪` / `NNs` / `n/m` / `∞` 表示。见 <c>docs/REAL_SAMPLES.md</c>。
    /// </summary>
    Plain = 1,
}

/// <summary>一行神器列表解析结果。</summary>
public sealed record ParsedRow(
    string ArtifactName,
    string PlayerName,
    ArtifactState State,
    int? CooldownSeconds,
    int? UsesRemaining,
    int? UsesTotal,
    string RawText,
    int Slot = 0,
    /// <summary>
    /// 社区服 HUD 自带的标号（如 `手电3` 的 `3`）。该服列表在神器被用掉后会**回流**（下方整体上移），
    /// 行槽位因此不再是稳定身份；而「名称 + 标号」是稳定的，Plain 模式据此做跟踪身份。
    /// 本服括号语法没有标号，为 null。
    /// </summary>
    int? ServerIndex = null);
