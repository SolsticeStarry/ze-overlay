using ZeOverlay.Shared;

namespace ZeOverlay.Stage.ScreenCapture;

/// <summary>S0 实现：基于 GDI 的截屏阶段。</summary>
public sealed class ScreenCaptureStage : IScreenCaptureStage
{
    private readonly Gdi _gdi = new();

    public string Name => "采集";

    public ImageFrame Process(PixelRect roi)
        => _gdi.TryCapture(roi, out ImageFrame? frame, out string? error) && frame is not null
            ? frame
            : throw new InvalidOperationException(error ?? "截屏失败。");
}
