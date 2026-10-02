using ZeOverlay.Shared;

namespace ZeOverlay.Stage.Presentation;

/// <summary>S7 契约：帧 + 条目 + 状态 → 展示结果。</summary>
public interface IPresentationStage : IStage<PresentationInput, PresentationResult>
{
}
