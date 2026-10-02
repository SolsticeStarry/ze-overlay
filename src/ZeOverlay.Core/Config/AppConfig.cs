namespace ZeOverlay.Core.Config;

/// <summary>
/// 顶层配置。PLAN 第 1.3 节：日志 / 截图 / 名单 / 配置固定在 exe 旁。
/// </summary>
public sealed class AppConfig
{
    public int Version { get; set; } = 1;

    public CaptureConfig Capture { get; set; } = new();

    public RoiConfig Roi { get; set; } = new();

    public HotkeyConfig Hotkeys { get; set; } = new();

    public OverlayConfig Overlay { get; set; } = new();

    public StorageConfig Storage { get; set; } = new();
}

/// <summary>穿透叠加窗口（PLAN 第 8.1 节）。</summary>
public sealed class OverlayConfig
{
    public bool Shown { get; set; } = true;

    /// <summary>叠加窗口左上角（屏幕像素）。</summary>
    public double X { get; set; } = 1380;

    public double Y { get; set; } = 240;

    public double FontSize { get; set; } = 18;

    public bool ShowUses { get; set; } = true;

    /// <summary>外推条目的显示颜色（实时条目用白字）。</summary>
    public string ExtrapolatedColor { get; set; } = "#FFA5F3FC";

    public string LiveColor { get; set; } = "#FFF5F5F0";
}

public sealed class CaptureConfig
{
    /// <summary>预览/采集刷新频率。M0 用 2Hz 方便肉眼观察；PLAN 目标为 1Hz。</summary>
    public int Fps { get; set; } = 2;

    /// <summary>框选所在显示器序号（单显示器固定为 0）。</summary>
    public int MonitorIndex { get; set; }

    public bool CaptureEnabled { get; set; } = true;
}

/// <summary>列表区域（ROI）标定结果。</summary>
public sealed class RoiConfig
{
    public bool IsSet { get; set; }

    /// <summary>框选时所在显示器的物理像素矩形，用于判断是否换了显示器。</summary>
    public string MonitorBounds { get; set; } = string.Empty;

    /// <summary>绝对屏幕物理像素矩形，x,y,w,h。</summary>
    public string ScreenRect { get; set; } = string.Empty;

    /// <summary>框选时刻目标窗口的矩形，x,y,w,h。</summary>
    public string WindowRectAtCapture { get; set; } = string.Empty;

    /// <summary>ROI 相对目标窗口的比例，x,y,w,h（0..1），用于自动跟随。</summary>
    public string WindowRelative { get; set; } = string.Empty;

    /// <summary>自动跟随匹配用：目标窗口标题子串。</summary>
    public string TargetWindowTitle { get; set; } = string.Empty;

    /// <summary>自动跟随匹配用：目标进程名（不含 .exe）。</summary>
    public string TargetWindowProcess { get; set; } = string.Empty;
}

public sealed class HotkeyConfig
{
    public string TogglePreview { get; set; } = "Ctrl+Alt+O";

    public string SelectRoi { get; set; } = "Ctrl+Alt+R";

    public string Snapshot { get; set; } = "Ctrl+Alt+S";

    public string Quit { get; set; } = "Ctrl+Alt+Q";

    /// <summary>显示/隐藏穿透叠加。</summary>
    public string ToggleOverlay { get; set; } = "Ctrl+Alt+H";

    /// <summary>进入/退出拖动模式（穿透窗口不能直接拖）。</summary>
    public string DragOverlay { get; set; } = "Ctrl+Alt+D";
}

public sealed class StorageConfig
{
    public string ShotsDirName { get; set; } = "shots";

    public string LogsDirName { get; set; } = "logs";

    /// <summary>截图滚动上限（张），超出后删最旧。</summary>
    public int MaxShots { get; set; } = 300;

    public long MaxLogBytes { get; set; } = 2 * 1024 * 1024;

    public int MaxLogFiles { get; set; } = 5;
}
