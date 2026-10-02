using ZeOverlay.Shared;

namespace ZeOverlay.Stage.Matching;

/// <summary>S6 契约：跟踪结果 → 待展示条目。</summary>
public interface IMatchingStage : IStage<TrackingResult, IReadOnlyList<DisplayEntry>>
{
}
