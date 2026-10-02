using ZeOverlay.Shared;

namespace ZeOverlay.Infrastructure;

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

    public RecognitionConfig Recognition { get; set; } = new();
}

/// <summary>PP-OCR 识别运行时参数（M6）。</summary>
public sealed class RecognitionConfig
{
    /// <summary>onnxruntime intra-op 线程数；0 = 交给运行时自动决定。</summary>
    public int IntraOpThreads { get; set; }

    /// <summary>
    /// 工作线程在两次推理之间是否自旋等待。默认关闭：
    /// 识别只有 1Hz，自旋会让常驻 CPU 白涨（见 docs/DESIGN.md §4 M6）。
    /// </summary>
    public bool AllowSpinning { get; set; }

    /// <summary>
    /// 执行提供程序："directml"（默认，DX12 GPU 通用加速）或 "cpu"。
    /// DML 初始化失败（无 DX12/驱动不兼容）会自动回退 CPU。
    /// </summary>
    public string ExecutionProvider { get; set; } = "directml";

    /// <summary>
    /// 固定输入宽度（0=关闭）。DML 对输入形状敏感：每换一次宽度就重编译算子，实测慢 3 倍。
    /// DML 下建议设 640~768（保持长宽比、右侧补灰，不改字形）。
    /// </summary>
    public int FixedInputWidth { get; set; }

    /// <summary>
    /// 每次送模型的批大小。0=自动（DML 用 12，CPU 用 1）。
    /// 实测 DML 全批 12 行 ~70ms，比逐行快 1.6×；CPU 上批处理反而更慢。
    /// </summary>
    public int BatchSize { get; set; }

    /// <summary>
    /// 识别刷新间隔（毫秒）。默认 1000；越小越跟手但越吃 CPU。
    /// Host 会把它钳制在 200–5000 ms。
    /// </summary>
    public int IntervalMs { get; set; } = 1000;
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

    /// <summary>
    /// 显示排序：`watchlist`（名单/显示顺序，默认）｜`slot`（页-行）｜
    /// `cooldown`（就绪优先 + 冷却升序）｜`name`（名称）。见 <see cref="ZeOverlay.Shared.OverlaySort"/>。
    /// </summary>
    public string SortMode { get; set; } = "watchlist";

    /// <summary>是否在条目前显示 `#页-行`。</summary>
    public bool ShowPageSlot { get; set; }

    /// <summary>是否为外推（本地推算）条目显示 `~` 标记。</summary>
    public bool ShowSourceMark { get; set; } = true;

    /// <summary>叠加背景不透明度 0.00–1.00（0 = 完全透明）。</summary>
    public double BackgroundOpacity { get; set; } = 0.09;
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
