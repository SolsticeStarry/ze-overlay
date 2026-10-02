using ZeOverlay.Shared;

namespace ZeOverlay.Stage.Recognition;

/// <summary>S3 契约：行裁剪 → 识别结果。</summary>
public interface IRecognitionStage : IStage<IReadOnlyList<RowCrop>, IReadOnlyList<RecognizedRow>>
{
}
