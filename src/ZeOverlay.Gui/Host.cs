using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using ZeOverlay.Shared;
using ZeOverlay.Infrastructure;
using ZeOverlay.Win32;
using ZeOverlay.Stage.ScreenCapture;
using ZeOverlay.Stage.RowAnalysis;
using ZeOverlay.Stage.GlyphSegmentation;
using ZeOverlay.Stage.Recognition;
using ZeOverlay.Stage.Parsing;
using ZeOverlay.Stage.Tracking;
using ZeOverlay.Stage.Matching;
using ZeOverlay.Stage.Presentation;

namespace ZeOverlay.Gui;

/// <summary>
/// M0 的编排：配置 → 标定 → 采集 → 预览 → 留档。
/// 采集跑在独立线程，UI 只负责显示（PLAN 第 10 节「采集/识别/UI 分离」）。
/// </summary>
public sealed partial class Host : IDisposable
{
    private readonly Paths _paths;
    private readonly Dispatcher _dispatcher;
    private readonly AppConfig _config;
    private readonly bool _selectRoiOnly;
    private readonly object _stateGate = new();

    private readonly IScreenCapture _capture = new Gdi();
    private readonly IOcrEngine _ocr = new SystemOcrEngine();
    private PpOcrEngine? _ppOcr;
    private int _ppOcrFixedWidth;
    private int _ppOcrBatchSize = 1;
    private IRowRecognizer? _recognizer;
    private Pipeline? _pipeline;
    private TrackingStage? _trackingStage;
    private string _recognitionName = string.Empty;
    private TrackerOptions _trackerOptions = new();
    private Tracker _tracker = new();
    private readonly object _trackerGate = new();
    private System.Threading.Timer? _overlayTick;
    private readonly Dictionary<string, int> _recentNameCounts = new(StringComparer.Ordinal);
    private readonly object _recentNamesGate = new();

    private Matcher _watchlist = new(null);
    private WatchlistConfig _watchlistConfig = new();
    private string _recognitionText = "（尚未识别）";
    private double _lastOcrMs;
    private DateTime _lastOcrWarnAt = DateTime.MinValue;
    private DateTime _lastUnparsedLogAt = DateTime.MinValue;
    private DateTime _lastOcrAt = DateTime.MinValue;
    private DateTime _lastEntryDumpAt = DateTime.MinValue;
    private DateTime _lastOverlayGuardAt = DateTime.MinValue;

    /// <summary>
    /// 当前是否识别到「连写服」（行带服务器标号）。用于**自动提速**：
    /// 连写/带标号服列表变化快，采集与识别按 <see cref="RecognitionConfig.CommunityIntervalMs"/> 提速（默认 300ms）；
    /// 括号服沿用配置值。倒计时由独立 tick 外推，不需要高频采集。
    /// </summary>
    private volatile bool _communityMode;

    /// <summary>跟踪表诊断的间隔。</summary>
    private static readonly TimeSpan EntryDumpInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 单行识别的最低置信度。低于它的读数多为 PP-OCR 的「幻觉」
    /// （实测 `皇家口粮` → `皇家口粮erdrinked`、`[R]4/5` → `/4/5` 这类乱码会造出重复条目）。
    /// 实测正常行的置信度在 0.86~0.95，所以 0.6 很安全。
    /// </summary>
    private const double MinRowConfidence = 0.6;

    private Log? _log;
    private Shots? _shots;
    private HotkeyManager? _hotkeys;
    private PreviewWindow? _preview;
    private OverlayWindow? _overlay;
    private Thread? _captureThread;

    private volatile bool _running;
    private volatile bool _disposed;
    private volatile ImageFrame? _lastFrame;
    private volatile string _lastError = string.Empty;
    private volatile string _followSummary = "(未设置跟随目标)";
    private PixelRect _roi = PixelRect.Empty;
    private double _lastCaptureMs;
    private long _frameCount;
    private double _measuredFps;
    private int _rowCount = -1;
    private double? _rowPitch;
    private Structure _structureTracker = new();
    private ImageFrame? _previousFrame;
    private readonly Cache _rowAnalysisCache = new();
    private int _changeSaveCount;
    private DateTime _lastChangeSaveAt = DateTime.MinValue;
    private volatile string _notice = "等待框选。";

    private static readonly TimeSpan ChangeSaveMinInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ContentChangeLogMinInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FollowWarnMinInterval = TimeSpan.FromSeconds(30);
    private DateTime _lastFollowWarnAt = DateTime.MinValue;

    private const string LabelTogglePreview = "显示/隐藏预览";
    private const string LabelSelectRoi = "重新框选";
    private const string LabelSnapshot = "保存截图";
    private const string LabelQuit = "退出";
    private const string LabelToggleOverlay = "显示/隐藏叠加";
    private const string LabelDragOverlay = "拖动叠加";

    /// <summary>变化前后帧单独存目录，避免被普通截图的上限滚动清理挤掉——那是要长期留存的样本。</summary>
    private const int ChangeShotCapacity = 200;
    private DateTime _lastContentChangeLogAt = DateTime.MinValue;
    private DateTime _lastOverflowWarnAt = DateTime.MinValue;
    private Shots? _changeShots;
    private readonly Dictionary<string, string> _resolvedHotkeys = new(StringComparer.Ordinal);

    public Host(Paths paths, Dispatcher dispatcher, bool selectRoiOnly = false)
    {
        _paths = paths;
        _dispatcher = dispatcher;
        _selectRoiOnly = selectRoiOnly;
        _config = ConfigStore.Load(paths.ConfigFile);
        ApplyStorageConfig();

        // 跟踪器与配置共享同一份选项对象：设置里改「行消失超时」可即时生效。
        _trackerOptions.DisappearAfterSeconds = Math.Clamp(_config.Tracking.RowDisappearSeconds, 0.5, 60);
        _tracker = new Tracker(_trackerOptions);
    }

    public void Start()
    {
        _log = new Log(
            _paths.LogsDirectory,
            "app.log",
            _config.Storage.MaxLogBytes,
            _config.Storage.MaxLogFiles);

        _shots = new Shots(_paths.ShotsDirectory, _config.Storage.MaxShots);

        WatchlistConfig watchlist = WatchlistStore.Load(_paths.WatchlistFile);

        // 把旧的「多档案」结构折叠成单配置：采用当前档案的 ROI/名单，并合并所有档案的名表。
        // 之后行语法逐行自动判断，不再需要人工选档案。
        ProfileDefaults.CollapseProfiles(_config, watchlist);
        ConfigStore.Save(_paths.ConfigFile, _config);
        WatchlistStore.Save(_paths.WatchlistFile, watchlist);
        ApplyRuntimeConfig(watchlist);

        _log?.Info(
            $"识别引擎={_ocr.Name}（可用={_ocr.IsAvailable}）；"
            + $"关注名单={_watchlist.Entries.Count} 项；阈值={_watchlistConfig.MatchThreshold:0.##}；"
            + $"名表={_config.Vocabulary.Count} 项；"
            + $"ROI={(string.IsNullOrWhiteSpace(_config.Roi.ScreenRect) ? "未标定" : _config.Roi.ScreenRect)}");

        // 优先用 PP-OCR（按行识别）；模型缺失或加载失败时回退系统 OCR。
        // 模型在 v6 → v4 → v3 里自动挑第一个存在的（v3 读括号数字会吞位/加位，见 PpOcrModels）。
        string modelsDirectory = Path.Combine(_paths.BaseDirectory, "models");
        PpOcrModels.Resolved resolved = PpOcrModels.Resolve(
            modelsDirectory, _config.Recognition.ModelFile, _config.Recognition.KeysFile);
        string modelPath = resolved.ModelPath;
        string keysPath = resolved.KeysPath;
        string engineLabel = resolved.Label;

        if (File.Exists(modelPath) && File.Exists(keysPath))
        {
            int? threads = _config.Recognition.IntraOpThreads > 0 ? _config.Recognition.IntraOpThreads : null;
            bool useDml = string.Equals(_config.Recognition.ExecutionProvider, "directml", StringComparison.OrdinalIgnoreCase);
            int fixedWidth = _config.Recognition.FixedInputWidth;
            int batchSize = _config.Recognition.BatchSize > 0
                ? _config.Recognition.BatchSize
                : (useDml ? 12 : 1);

            // DML 对输入形状敏感（换宽度即重编译），未显式配置时给一个安全默认。
            if (useDml && fixedWidth <= 0)
            {
                fixedWidth = 640;
            }

            try
            {
                _ppOcr = new PpOcrEngine(modelPath, keysPath, threads, _config.Recognition.AllowSpinning,
                    useDml ? OcrExecutionProvider.DirectML : OcrExecutionProvider.Cpu, label: engineLabel);
                _log?.Info(
                    $"PP-OCR 已加载：{_ppOcr.Name}（模型={Path.GetFileName(modelPath)} 字典={Path.GetFileName(keysPath)}；"
                    + $"按行识别，不需要检测模型；"
                    + $"线程={(threads is { } t ? t.ToString(CultureInfo.InvariantCulture) : "自动")}；"
                    + $"自旋={(_config.Recognition.AllowSpinning ? "开" : "关")}；"
                    + $"固定宽度={(fixedWidth > 0 ? fixedWidth.ToString(CultureInfo.InvariantCulture) : "关")}；"
                    + $"批大小={batchSize}）");
            }
            catch (Exception ex) when (useDml)
            {
                // DX12/驱动不可用时不能因为一块 GPU 把识别整个废掉，回退 CPU。
                _log?.Warn($"DirectML 会话创建失败，回退 CPU EP：{ex.Message}");
                try
                {
                    _ppOcr = new PpOcrEngine(modelPath, keysPath, threads, _config.Recognition.AllowSpinning, OcrExecutionProvider.Cpu, label: engineLabel);
                    fixedWidth = 0;
                    batchSize = 1;
                    _log?.Info($"PP-OCR 已回退 CPU：{_ppOcr.Name}");
                }
                catch (Exception fallbackEx)
                {
                    _log?.Error("加载 PP-OCR 模型失败，回退系统 OCR", fallbackEx);
                    _ppOcr = null;
                }
            }
            catch (Exception ex)
            {
                _log?.Error("加载 PP-OCR 模型失败，回退系统 OCR", ex);
                _ppOcr = null;
            }

            _ppOcrFixedWidth = _ppOcr is not null ? fixedWidth : 0;
            _ppOcrBatchSize = _ppOcr is not null ? batchSize : 1;
        }
        else
        {
            _log?.Warn($"未找到 PP-OCR 模型（{modelPath}），回退系统 OCR。");
        }

        if (!_ocr.IsAvailable)
        {
            _log?.Warn("系统 OCR 不可用：只能做行剖分，无法识别文字。请确认已安装中文语言包。");
        }

        // 系统 OCR 回退路径（行级）；PP-OCR 主路径走 Pipeline。
        if (_ppOcr is null && _ocr.IsAvailable)
        {
            _recognizer = new SystemOcrRecognizer(_ocr);
        }

        // 主路径：PP-OCR 时用阶段化 Pipeline（S1–S6）。
        BuildPipeline();
        _changeShots = new Shots(
            Path.Combine(_paths.ShotsDirectory, "changes"),
            ChangeShotCapacity);

        LogEnvironment();
        RestoreCalibration();
        VerifyRestoredRoi();

        _preview = new PreviewWindow();
        _preview.BindActions(SelectRoi, Snapshot, OpenShots, OpenLogs, Quit);
        _preview.InitializeSettings(_config, _watchlistConfig, SnapshotRecentNames);
        _preview.SettingsApplied += (_, _) => ApplySettingsFromPanel();
        _preview.Closed += (_, _) => Quit();
        _preview.Show();

        _overlay = new OverlayWindow();
        _overlay.SetPosition(_config.Overlay.X, _config.Overlay.Y);
        if (_config.Overlay.Shown)
        {
            _overlay.Show();
        }

        RegisterHotkeys();

        _running = true;
        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "ZeOverlay.Capture",
        };
        _captureThread.Start();

        _log?.Info("启动完成；采集后端=" + _capture.Name + "；热键=" + DescribeHotkeys());

        // 倒计时/叠加**独立刷新**：不等 OCR/采集节奏，按墙钟定期重建（纯外推，开销极小）。
        // 这样即使 OCR 变慢或某帧没识别出来，倒计时也照常走、不会滞后。
        _overlayTick = new System.Threading.Timer(_ => RefreshOverlayTick(), null, 200, 200);

        if (_selectRoiOnly)
        {
            // 「只做标定」模式：进入框选，确认或取消后立即退出。
            _dispatcher.BeginInvoke(() =>
            {
                SelectRoi();
                Quit();
            });
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _running = false;

        try
        {
            _captureThread?.Join(TimeSpan.FromSeconds(2));
        }
        catch (ThreadStateException)
        {
        }

        _hotkeys?.Dispose();
        _overlayTick?.Dispose();
        _overlay?.Close();
        _ppOcr?.Dispose();
        _ocr.Dispose();
        _capture.Dispose();
        _log?.Info("已退出。");
        _log?.Dispose();
    }

    /// <summary>装配 PP-OCR 主路径的阶段化管线；无 PP-OCR 时置空（回退系统 OCR 的旧路径）。</summary>
    private void BuildPipeline()
    {
        if (_ppOcr is null)
        {
            _pipeline = null;
            _trackingStage = null;
            return;
        }

        _recognitionName = _ppOcr.Name;
        _trackingStage = new TrackingStage(_tracker);
        _pipeline = new Pipeline(
            new RowAnalysisStage(),
            // 全宽裁剪：社区服的简称列又小又暗，紧缩到字形会把整列裁掉；全宽对本服也实测 12/12。
            new GlyphSegmentationStage(useBandWidth: true),
            new PpOcrRecognitionStage(_ppOcr, _ppOcrFixedWidth, _ppOcrBatchSize),
            // 逐行自动判断行格式（括号 / 连写）；名表用于社区服切分与挡 OCR 读花。
            new ParsingStage(vocabulary: _config.Vocabulary),
            _trackingStage,
            new MatchingStage(_watchlist, _watchlistConfig.SortOrder));
    }

    /// <summary>
    /// 把单份配置装进运行时：名单、跟踪器、ROI。行语法逐行自动判断，不再需要档案切换。
    /// </summary>
    private void ApplyRuntimeConfig(WatchlistConfig watchlist)
    {
        _watchlistConfig = watchlist;
        _watchlist = new Matcher(
            watchlist.Names,
            new WatchlistOptions { Threshold = watchlist.MatchThreshold });

        _trackerOptions = new TrackerOptions
        {
            DisappearAfterSeconds = Math.Clamp(_config.Tracking.RowDisappearSeconds, 0.5, 60),
        };
        _tracker = new Tracker(_trackerOptions);

        lock (_stateGate)
        {
            _roi = PixelRect.Parse(_config.Roi.ScreenRect);
        }
    }

    private void ApplyStorageConfig()
    {
        _paths.ShotsDirName = _config.Storage.ShotsDirName;
        _paths.LogsDirName = _config.Storage.LogsDirName;
        _paths.EnsureDirectories();
    }

    private void LogEnvironment()
    {
        foreach (MonitorInfo monitor in MonitorService.Enumerate())
        {
            _log?.Info(string.Create(
                CultureInfo.InvariantCulture,
                $"显示器 #{monitor.Index} {monitor.DeviceName} rect={monitor.Bounds} work={monitor.WorkArea} dpi={monitor.Dpi} scale={monitor.Scale:0.##} primary={monitor.IsPrimary}"));
        }

        _log?.Info($"采集后端={_capture.Name}；配置={_paths.ConfigFile}；截图目录={_paths.ShotsDirectory}");
    }

    private void RestoreCalibration()
    {
        RoiConfig profileRoi = _config.Roi;
        if (!profileRoi.IsSet)
        {
            return;
        }

        PixelRect roi = PixelRect.Parse(profileRoi.ScreenRect);
        if (roi.IsEmpty)
        {
            return;
        }

        // 恢复时也校验：配置可能被手改，或上次标定因跟随目标选错而写入了屏幕外矩形。
        MonitorInfo monitor = MonitorService.FromPoint(roi.X + roi.Width / 2, roi.Y + roi.Height / 2);
        if (roi.Intersect(monitor.Bounds).Area < roi.Area / 2)
        {
            _notice = $"config.json 里的 ROI {roi} 大部分在屏幕外，已忽略。请重新框选。";
            _log?.Warn($"[恢复] 忽略越界 ROI={roi}（显示器 {monitor.Bounds}）。");
            return;
        }

        lock (_stateGate)
        {
            _roi = roi;
        }

        _notice = $"已从 config.json 恢复 ROI：{roi}";
        _log?.Info($"恢复 ROI={roi}；窗口相对={profileRoi.WindowRelative}；目标={profileRoi.TargetWindowProcess}");
    }

    /// <summary>启动时对已保存的 ROI 也做一次校验：配置可能被手改，或显示器/窗口已变化。</summary>
    private void VerifyRestoredRoi()
    {
        PixelRect roi;
        lock (_stateGate)
        {
            roi = _roi;
        }

        if (roi.IsEmpty)
        {
            return;
        }

        string? warning = ProbeRoiRegions(roi);
        if (warning is not null)
        {
            _notice += "　⚠ " + warning;
            _log?.Warn("[启动校验] " + warning);
        }
    }

}
