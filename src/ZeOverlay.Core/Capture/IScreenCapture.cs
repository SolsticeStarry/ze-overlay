using ZeOverlay.Core.Geometry;
using ZeOverlay.Core.Imaging;

namespace ZeOverlay.Core.Capture;

/// <summary>
/// 截屏后端抽象。PLAN 第 10 节的降级链（CUDA → CPU → 系统 OCR → 保留上帧）
/// 同样适用于采集层：WGC → 桌面复制 → GDI。每层可独立替换。
/// </summary>
public interface IScreenCapture : IDisposable
{
    /// <summary>后端名称，用于日志与 UI 展示。</summary>
    string Name { get; }

    /// <summary>
    /// 截取给定屏幕物理像素区域。失败时返回 false 并给出原因，而不是抛异常，
    /// 以便上层按 PLAN 第 7 节「失败保留上帧」处理。
    /// </summary>
    bool TryCapture(PixelRect region, out ImageFrame? frame, out string? error);
}
