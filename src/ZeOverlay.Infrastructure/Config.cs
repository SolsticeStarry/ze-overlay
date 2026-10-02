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

    public TrackingConfig Tracking { get; set; } = new();

    /// <summary>
    /// 服务器档案表（一份程序多档案）。每个档案自带解析语法 / ROI / 名单 / 分页参数，
    /// 运行时按神器名自动选档，也可手动切换。空表 ⇒ 首次启动时由旧配置迁移出一个档案。
    /// </summary>
    public List<ServerProfileConfig> Profiles { get; set; } = [];

    /// <summary>当前生效的档案 Id；为空时取第一个档案。（旧结构，已不参与运行，仅作迁移来源）</summary>
    public string ActiveProfileId { get; set; } = string.Empty;

    /// <summary>
    /// 神器名表（**格式自动判断**用）：社区服 Plain 语法用它切分「简称 / 玩家名」，
    /// 并挡掉 OCR 读花的名称。可把多个服的名字并在一起。
    /// </summary>
    public List<string> Vocabulary { get; set; } = [];
}

/// <summary>
/// 一个社区服档案。把原先散落在全局的社区相关假设（解析语法、ROI、名单、单页行数、是否翻页）
/// 收拢到一起，便于同时适配多个服。
/// </summary>
public sealed class ServerProfileConfig
{
    public string Id { get; set; } = "default";

    public string DisplayName { get; set; } = "本服";

    /// <summary>解析语法："bracket"（本服，默认）| "plain"（社区服，无方括号）。</summary>
    public string ParserMode { get; set; } = "bracket";

    /// <summary>该服是否有翻页（列表超过单页换第 2 页）。社区服实测无翻页。</summary>
    public bool PagingEnabled { get; set; } = true;

    /// <summary>单页最大行数；超过即视为量测不可信（ROI 混进了非列表内容）。</summary>
    public int MaxRowsPerPage { get; set; } = ListRules.MaxRowsPerPage;

    /// <summary>该档案的采集频率（Hz）；0 = 用全局 <see cref="CaptureConfig.Fps"/>。</summary>
    public int CaptureFps { get; set; }

    /// <summary>该档案的识别间隔（毫秒）；0 = 用全局 <see cref="RecognitionConfig.IntervalMs"/>。</summary>
    public int RecognitionIntervalMs { get; set; }

    /// <summary>该档案的行消失超时（秒）；0 = 用全局 <see cref="TrackingConfig.RowDisappearSeconds"/>。</summary>
    public double RowDisappearSeconds { get; set; }

    /// <summary>该服列表区域（HUD 位置不同，各存一份）。</summary>
    public RoiConfig Roi { get; set; } = new();

    /// <summary>该服的关注名单；为空 = 全部显示。</summary>
    public List<string> Watchlist { get; set; } = [];

    /// <summary>显示分组顺序。</summary>
    public List<string> SortOrder { get; set; } = [];

    public double MatchThreshold { get; set; } = 0.85;

    /// <summary>
    /// 自动选档用的神器名称表（该服全部神器名）。为空时回退用 <see cref="Watchlist"/>。
    /// 它应尽量完整，不随用户的关注名单变化。
    /// </summary>
    public List<string> Vocabulary { get; set; } = [];
}

/// <summary>跟踪/消亡参数（M5）。</summary>
public sealed class TrackingConfig
{
    /// <summary>
    /// 某行超过这么多秒没扫到数据就自动消失。默认 6s（0.5–60）。无条件：翻页/列表消失也算。
    /// </summary>
    public double RowDisappearSeconds { get; set; } = 6.0;
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

    /// <summary>条目正文颜色（实时与外推**统一**使用，不再按来源换色）。</summary>
    public string LiveColor { get; set; } = "#FFF5F5F0";

    /// <summary>未扫描到（本地外推）标记 `~` 的颜色（默认亮蓝色）。</summary>
    public string ExtrapolatedColor { get; set; } = "#FF38BDF8";

    /// <summary>
    /// 显示排序：`watchlist`（名单/显示顺序，默认）｜`slot`（页-行）｜
    /// `cooldown`（就绪优先 + 冷却升序）｜`name`（名称）。见 <see cref="ZeOverlay.Shared.OverlaySort"/>。
    /// </summary>
    public string SortMode { get; set; } = "watchlist";

    /// <summary>是否在条目前显示 `#页-行`。</summary>
    public bool ShowPageSlot { get; set; }

    /// <summary>
    /// 是否显示玩家名。玩家名放在最左的**固定宽度列**（`▲`/`~` 标记左侧），
    /// 因此不会改变右侧文字的位置。默认开。
    /// </summary>
    public bool ShowPlayerName { get; set; } = true;

    /// <summary>玩家名列宽度（像素）。固定宽度保证右侧排版不随玩家名长度移动。</summary>
    public double PlayerNameWidth { get; set; } = 110;

    /// <summary>是否为未扫描到（本地推算）的条目显示最左 `▲` 标记。</summary>
    public bool ShowSourceMark { get; set; } = true;

    /// <summary>
    /// 无数字标号时，是否自动按行从 1 编号（将来社区自带标号时不会叠加）。
    /// </summary>
    public bool ShowRowNumber { get; set; } = true;

    /// <summary>行间距（像素）：每行上下追加的间距，可为负以收紧。默认 2。</summary>
    public double RowSpacing { get; set; } = 2.0;

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

    /// <summary>切换到下一个服务器档案（用于给新服框选 ROI / 手动纠正自动选档）。</summary>
    public string CycleProfile { get; set; } = "Ctrl+Alt+P";
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
