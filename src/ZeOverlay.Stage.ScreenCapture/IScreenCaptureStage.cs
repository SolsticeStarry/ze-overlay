using ZeOverlay.Shared;

namespace ZeOverlay.Stage.ScreenCapture;

/// <summary>S0 契约：屏幕区域 → 帧。</summary>
public interface IScreenCaptureStage : IStage<PixelRect, ImageFrame>
{
}

/// <summary>截屏后端抽象（平台解耦）。</summary>
public interface IScreenCapture : IDisposable
{
    string Name { get; }

    bool TryCapture(PixelRect region, out ImageFrame? frame, out string? error);
}
