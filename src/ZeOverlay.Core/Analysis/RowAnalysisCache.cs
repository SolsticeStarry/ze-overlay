using ZeOverlay.Core.Imaging;

namespace ZeOverlay.Core.Analysis;

/// <summary>仅对尺寸及全部像素相同的帧复用行分析；不缓存 OCR 或跟踪状态。</summary>
public sealed class RowAnalysisCache
{
    private ImageFrame? _frame;
    private RowProfileReport? _report;

    public RowProfileReport Analyze(ImageFrame frame, Func<ImageFrame, RowProfileReport> analyze)
    {
        if (_frame is not null && _report is not null
            && _frame.Width == frame.Width && _frame.Height == frame.Height
            && _frame.Bgra.AsSpan(0, _frame.ByteCount).SequenceEqual(frame.Bgra.AsSpan(0, frame.ByteCount)))
        {
            return _report;
        }

        // 分析失败不能提交缓存，否则同一帧将无法重试。ImageFrame 像素发布后不可修改。
        RowProfileReport report = analyze(frame);
        _frame = frame;
        _report = report;
        return report;
    }
}
