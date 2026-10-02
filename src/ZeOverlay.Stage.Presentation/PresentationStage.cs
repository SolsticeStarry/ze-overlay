using ZeOverlay.Shared;

namespace ZeOverlay.Stage.Presentation;

/// <summary>
/// S7 实现：把条目交给宿主提供的渲染回调（WPF 控件由组合根持有，
/// 由宿主在 UI 线程调用本阶段）。
/// </summary>
public sealed class PresentationStage : IPresentationStage
{
    private readonly Action<PresentationInput>? _render;

    public PresentationStage(Action<PresentationInput>? render = null)
    {
        _render = render;
    }

    public string Name => "展示";

    public PresentationResult Process(PresentationInput input)
    {
        _render?.Invoke(input);
        return new PresentationResult(string.Empty);
    }
}
