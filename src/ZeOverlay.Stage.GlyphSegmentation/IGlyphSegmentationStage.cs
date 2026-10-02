using ZeOverlay.Shared;

namespace ZeOverlay.Stage.GlyphSegmentation;

/// <summary>S2 契约：(帧, 行报告) → 行裁剪。</summary>
public interface IGlyphSegmentationStage : IStage<RowInput, IReadOnlyList<RowCrop>>
{
}
