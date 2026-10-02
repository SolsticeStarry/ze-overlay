
using ZeOverlay.Shared;

namespace ZeOverlay.Stage.RowAnalysis;

/// <summary>仅对尺寸及全部像素相同的帧复用行分析；不缓存 OCR 或跟踪状态。</summary>
public sealed class Cache
{
    private ImageFrame? _frame;
    private RowReport? _report;

    public RowReport Analyze(ImageFrame frame, Func<ImageFrame, RowReport> analyze)
    {
        if (_frame is not null && _report is not null
            && _frame.Width == frame.Width && _frame.Height == frame.Height
            && _frame.Bgra.AsSpan(0, _frame.ByteCount).SequenceEqual(frame.Bgra.AsSpan(0, frame.ByteCount)))
        {
            return _report;
        }

        // 分析失败不能提交缓存，否则同一帧将无法重试。ImageFrame 像素发布后不可修改。
        RowReport report = analyze(frame);
        _frame = frame;
        _report = report;
        return report;
    }
}
