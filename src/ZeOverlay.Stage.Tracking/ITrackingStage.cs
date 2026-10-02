using ZeOverlay.Shared;

namespace ZeOverlay.Stage.Tracking;

/// <summary>S5 契约：本帧解析 + 行数 → 跟踪结果。</summary>
public interface ITrackingStage : IStage<TrackingInput, TrackingResult>
{
}
