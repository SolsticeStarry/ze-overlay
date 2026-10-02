using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ZeOverlay.App.Imaging;
using ZeOverlay.App.Input;
using ZeOverlay.Core.Analysis;
using ZeOverlay.Core.Capture;
using ZeOverlay.Core.Config;
using ZeOverlay.Core.Diagnostics;
using ZeOverlay.Core.Domain;
using ZeOverlay.Core.Geometry;
using ZeOverlay.Core.Imaging;
using ZeOverlay.Core.Matching;
using ZeOverlay.Core.Parsing;
using ZeOverlay.Core.Tracking;
using ZeOverlay.Platform.Windows;

namespace ZeOverlay.App;

/// <summary>
/// M0 的编排：配置 → 标定 → 采集 → 预览 → 留档。
/// 采集跑在独立线程，UI 只负责显示（PLAN 第 10 节「采集/识别/UI 分离」）。
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly AppPaths _paths;
    private readonly Dispatcher _dispatcher;
    private readonly AppConfig _config;
    private readonly bool _selectRoiOnly;
    private readonly object _stateGate = new();

    private readonly IScreenCapture _capture = new GdiScreenCapture();
    private readonly IOcrEngine _ocr = new WindowsMediaOcrEngine();
    private PpOcrRecEngine? _ppOcr;
    private int _ppOcrFixedWidth;
    private int _ppOcrBatchSize = 1;
    private readonly ArtifactTracker _tracker = new();
    private readonly Dictionary<string, int> _recentNameCounts = new(StringComparer.Ordinal);
    private readonly object _recentNamesGate = new();

    private WatchlistMatcher _watchlist = new(null);
    private WatchlistConfig _watchlistConfig = new();
    private string _recognitionText = "（尚未识别）";
    private double _lastOcrMs;
    private DateTime _lastOcrWarnAt = DateTime.MinValue;
    private DateTime _lastUnparsedLogAt = DateTime.MinValue;
    private DateTime _lastOcrAt = DateTime.MinValue;
    private DateTime _lastEntryDumpAt = DateTime.MinValue;
    private DateTime _lastOverlayGuardAt = DateTime.MinValue;

    /// <summary>跟踪表诊断的间隔。</summary>
    private static readonly TimeSpan EntryDumpInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 单行识别的最低置信度。低于它的读数多为 PP-OCR 的「幻觉」
    /// （实测 `皇家口粮` → `皇家口粮erdrinked`、`[R]4/5` → `/4/5` 这类乱码会造出重复条目）。
    /// 实测正常行的置信度在 0.86~0.95，所以 0.6 很安全。
    /// </summary>
    private const double MinRowConfidence = 0.6;

    /// <summary>识别间隔。PP-OCR 12 行约 0.8s，跑太密会拖垮采集循环；外推会补上间隔。</summary>
    private static readonly TimeSpan OcrInterval = TimeSpan.FromMilliseconds(1000);
    private RollingFileLog? _log;
    private ShotStore? _shots;
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
    private readonly ListStructureTracker _structureTracker = new();
    private ImageFrame? _previousFrame;
    private readonly RowAnalysisCache _rowAnalysisCache = new();
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
    private ShotStore? _changeShots;
    private readonly Dictionary<string, string> _resolvedHotkeys = new(StringComparer.Ordinal);

    public AppHost(AppPaths paths, Dispatcher dispatcher, bool selectRoiOnly = false)
    {
        _paths = paths;
        _dispatcher = dispatcher;
        _selectRoiOnly = selectRoiOnly;
        _config = ConfigStore.Load(paths.ConfigFile);
        ApplyStorageConfig();
    }

    public void Start()
    {
        _log = new RollingFileLog(
            _paths.LogsDirectory,
            "app.log",
            _config.Storage.MaxLogBytes,
            _config.Storage.MaxLogFiles);

        _shots = new ShotStore(_paths.ShotsDirectory, _config.Storage.MaxShots);

        WatchlistConfig watchlist = WatchlistStore.Load(_paths.WatchlistFile);
        _watchlistConfig = watchlist;
        _watchlist = new WatchlistMatcher(
            watchlist.Names,
            new WatchlistMatcherOptions { Threshold = watchlist.MatchThreshold });

        _log?.Info($"识别引擎={_ocr.Name}（可用={_ocr.IsAvailable}）；关注名单={_watchlist.Entries.Count} 项；阈值={watchlist.MatchThreshold:0.##}");

        // 优先用 PP-OCR（按行识别）；模型缺失或加载失败时回退系统 OCR。
        string modelPath = Path.Combine(_paths.BaseDirectory, "models", "ch_PP-OCRv3_rec_infer.onnx");
        string keysPath = Path.Combine(_paths.BaseDirectory, "models", "ppocr_keys_v1.txt");

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
                _ppOcr = new PpOcrRecEngine(modelPath, keysPath, threads, _config.Recognition.AllowSpinning,
                    useDml ? OcrExecutionProvider.DirectML : OcrExecutionProvider.Cpu);
                _log?.Info(
                    $"PP-OCR 已加载：{_ppOcr.Name}（按行识别，不需要检测模型；"
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
                    _ppOcr = new PpOcrRecEngine(modelPath, keysPath, threads, _config.Recognition.AllowSpinning, OcrExecutionProvider.Cpu);
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
        _changeShots = new ShotStore(
            Path.Combine(_paths.ShotsDirectory, "changes"),
            ChangeShotCapacity);

        LogEnvironment();
        RestoreCalibration();
        VerifyRestoredRoi();

        _preview = new PreviewWindow();
        _preview.BindActions(SelectRoi, Snapshot, OpenShots, OpenLogs, Quit, OpenSettings);
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
        _overlay?.Close();
        _ppOcr?.Dispose();
        _ocr.Dispose();
        _capture.Dispose();
        _log?.Info("已退出。");
        _log?.Dispose();
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
        if (!_config.Roi.IsSet)
        {
            return;
        }

        PixelRect roi = PixelRect.Parse(_config.Roi.ScreenRect);
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
        _log?.Info($"恢复 ROI={roi}；窗口相对={_config.Roi.WindowRelative}；目标={_config.Roi.TargetWindowProcess}");
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

    private void RegisterHotkeys()
    {
        IntPtr handle = new WindowInteropHelper(_preview!).Handle;
        HwndSource? source = HwndSource.FromHwnd(handle);
        if (source is null)
        {
            _log?.Error("无法获取窗口消息源，全局热键不可用（按钮仍可用）。");
            _notice = "全局热键不可用，请用界面按钮操作。";
            return;
        }

        _hotkeys = new HotkeyManager(source);

        // 首选手势取自 config.json；被系统内其他程序占用时按候选顺序回退，
        // 实测本机 Ctrl+Alt+R 已被占用，故必须有回退，否则会丢掉「重新框选」入口。
        RegisterWithFallback(LabelTogglePreview, _config.Hotkeys.TogglePreview, ["Ctrl+Shift+O", "Ctrl+Alt+F9"], TogglePreview);
        RegisterWithFallback(LabelSelectRoi, _config.Hotkeys.SelectRoi, ["Ctrl+Shift+R", "Ctrl+Alt+F2"], SelectRoi);
        RegisterWithFallback(LabelSnapshot, _config.Hotkeys.Snapshot, ["Ctrl+Shift+S", "Ctrl+Alt+F3"], Snapshot);
        RegisterWithFallback(LabelQuit, _config.Hotkeys.Quit, ["Ctrl+Shift+Q", "Ctrl+Alt+F12"], Quit);
        RegisterWithFallback(LabelToggleOverlay, _config.Hotkeys.ToggleOverlay, ["Ctrl+Shift+H", "Ctrl+Alt+F5"], ToggleOverlay);
        RegisterWithFallback(LabelDragOverlay, _config.Hotkeys.DragOverlay, ["Ctrl+Shift+D", "Ctrl+Alt+F6"], ToggleOverlayDragMode);

        // 提示必须用**实际生效**的手势，否则用户按提示操作会没反应。
        _preview?.SetHotkeyHints(HotkeyHint(LabelSelectRoi), HotkeyHint(LabelSnapshot));
    }

    /// <summary>取某个动作实际生效的热键；全部被占用时给出可读说明。</summary>
    private string HotkeyHint(string label)
        => _resolvedHotkeys.TryGetValue(label, out string? gesture) && !gesture.StartsWith('(')
            ? gesture
            : "界面按钮";

    private void RegisterWithFallback(string label, string preferred, string[] fallbacks, Action action)
    {
        foreach (string gesture in new[] { preferred }.Concat(fallbacks))
        {
            if (string.IsNullOrWhiteSpace(gesture))
            {
                continue;
            }

            if (_hotkeys!.TryRegister(gesture, action, out string? error))
            {
                _resolvedHotkeys[label] = gesture;
                _log?.Info($"热键已注册：{label} = {gesture}");
                return;
            }

            _log?.Warn($"热键候选被占用：{label} = {gesture}；{error}");
        }

        _resolvedHotkeys[label] = "(全部被占用)";
        _log?.Warn($"热键 {label} 的所有候选均不可用；界面按钮仍然可用。");
    }

    private string DescribeHotkeys()
    {
        if (_resolvedHotkeys.Count == 0)
        {
            return "(不可用，请用界面按钮)";
        }

        return string.Join(
            "  ",
            _resolvedHotkeys.Select(pair => $"{pair.Key}={pair.Value}"));
    }

    // ---------- 操作 ----------

    private void SelectRoi()
    {
        if (_disposed)
        {
            return;
        }

        // 记录「按下热键时正在前台的程序」，作为自动跟随的目标窗口。
        WindowInfo? foreground = WindowLocator.Foreground();

        // 绝不能把本程序自己的窗口当成跟随目标：刚启动/刚重启时预览窗口常在前台，
        // 一旦记成目标，ROI 会被换算成相对本程序的比例，进而算出屏幕外的垃圾矩形。
        if (foreground is not null && foreground.ProcessId == Environment.ProcessId)
        {
            _log?.Warn($"[框选] 前台是本程序自身（{foreground.ProcessName}），不作为跟随目标。");
            foreground = null;
        }

        var selector = new RoiSelectorWindow();
        selector.Trace = message => _log?.Info("[框选] " + message);
        bool? result = selector.ShowDialog();

        if (result != true)
        {
            _log?.Warn($"[框选] 未确认（ShowDialog 返回 {result}）。");
            _notice = "已取消框选。";
            return;
        }

        ApplyCalibration(selector.SelectedRect, foreground);
    }

    private void ApplyCalibration(PixelRect roi, WindowInfo? target)
    {
        if (roi.IsEmpty)
        {
            return;
        }

        PixelRect reference = target is { Bounds.IsEmpty: false }
            ? target.Bounds
            : MonitorService.FromPoint(roi.X + roi.Width / 2, roi.Y + roi.Height / 2).Bounds;

        // 标定结果必须基本落在显示器内，否则不保存（例如跟随目标选错导致的屏幕外矩形）。
        MonitorInfo containing = MonitorService.FromPoint(roi.X + roi.Width / 2, roi.Y + roi.Height / 2);
        if (roi.Intersect(containing.Bounds).Area < roi.Area / 2)
        {
            _notice = $"ROI {roi} 大部分在屏幕外（显示器 {containing.Bounds}），未保存。请把目标程序切到前台后重新框选。";
            _log?.Warn($"[框选] 拒绝保存：{_notice}");
            return;
        }

        lock (_stateGate)
        {
            _roi = roi;
        }

        RoiConfig cfg = _config.Roi;
        cfg.IsSet = true;
        cfg.ScreenRect = roi.ToString();
        cfg.MonitorBounds = MonitorService.FromPoint(roi.X + roi.Width / 2, roi.Y + roi.Height / 2).Bounds.ToString();
        cfg.WindowRectAtCapture = reference.ToString();
        cfg.WindowRelative = RelativeRect.FromAbsolute(roi, reference).ToString();
        cfg.TargetWindowProcess = target?.ProcessName ?? string.Empty;
        cfg.TargetWindowTitle = target?.Title ?? string.Empty;

        ConfigStore.Save(_paths.ConfigFile, _config);

        _notice = $"ROI 已更新：{roi}（目标窗口：{WindowLocator.Describe(target)}）";
        _log?.Info($"标定完成 ROI={roi} 参考窗口={reference} 相对={cfg.WindowRelative} 目标={cfg.TargetWindowProcess}");

        // 标定即校验：立刻抓一帧，确认 ROI 里只有列表这一个横向区域。
        string? regionWarning = ProbeRoiRegions(roi);
        if (regionWarning is not null)
        {
            _notice += "　⚠ " + regionWarning;
            _log?.Warn("[ROI 校验] " + regionWarning);
        }

        _preview?.ShowFrame(BitmapPresenter.ToBitmapSource(_lastFrame), BuildStatus(), _recognitionText);
    }

    /// <summary>
    /// 抓一帧分析新 ROI。返回非 null 表示 ROI 疑似框宽（横向出现多个不相连的墨迹区域），
    /// 最常见的原因是连左下角聊天栏一起框了进来——那部分每帧都在变，
    /// 会让 PLAN 第 10 节的「ROI 无变化则复用上帧」永远无法命中。
    /// </summary>
    private string? ProbeRoiRegions(PixelRect roi)
    {
        if (!_capture.TryCapture(roi, out ImageFrame? frame, out string? error) || frame is null)
        {
            _log?.Warn($"[ROI 校验] 抓帧失败，跳过校验：{error}");
            return null;
        }

        try
        {
            RowProfileReport report = RowProfileAnalyzer.Analyze(frame);
            _log?.Info(
                $"[ROI 校验] 稳健行数={report.RowCount} 检出={report.Bands.Count} 离群={report.OutlierBands.Count} "
                + $"行距={report.Grid?.Pitch:0.##} 横向区域={report.RegionCount}");

            var warnings = new List<string>();
            if (report.RegionWarning is not null)
            {
                warnings.Add(report.RegionWarning);
            }

            warnings.AddRange(ListRules.ValidateRoi(report.RowCount, report.Grid?.Pitch, roi.Height));

            return warnings.Count == 0 ? null : string.Join("　", warnings);
        }
        catch (Exception ex)
        {
            _log?.Error("[ROI 校验] 分析失败", ex);
            return null;
        }
    }

    private void Snapshot()
    {
        ImageFrame? frame = _lastFrame;
        if (frame is null)
        {
            _notice = "暂无可用帧，无法保存截图。";
            return;
        }

        try
        {
            string path = _shots!.Save(frame, "roi");
            _notice = $"已保存截图：{path}";
            _log?.Info($"保存截图 {path}（{frame.Width}x{frame.Height}）");
        }
        catch (Exception ex)
        {
            _notice = $"保存截图失败：{ex.Message}";
            _log?.Error("保存截图失败", ex);
        }
    }

    private void TogglePreview()
    {
        if (_preview is null)
        {
            return;
        }

        if (_preview.IsVisible)
        {
            _preview.Hide();
            _notice = "预览窗口已隐藏（Ctrl+Alt+O 恢复）。";
        }
        else
        {
            _preview.Show();
            _preview.Activate();
            _notice = "预览窗口已显示。";
        }
    }

    private void OpenShots() => _preview?.OpenInExplorer(_paths.ShotsDirectory, "截图目录尚未创建。");

    private void OpenLogs() => _preview?.OpenInExplorer(_paths.LogsDirectory, "日志目录尚未创建。");

    private void OpenSettings()
    {
        if (_disposed)
        {
            return;
        }

        var window = new SettingsWindow(_watchlistConfig, SnapshotRecentNames);
        window.Owner = _preview;
        if (window.ShowDialog() != true)
        {
            return;
        }

        _watchlistConfig = window.Configuration;
        WatchlistStore.Save(_paths.WatchlistFile, _watchlistConfig);
        _watchlist = new WatchlistMatcher(
            _watchlistConfig.Names,
            new WatchlistMatcherOptions { Threshold = _watchlistConfig.MatchThreshold });
        _notice = $"关注名单已保存：{_watchlist.Entries.Count} 项，阈值 {_watchlistConfig.MatchThreshold:0.##}";
        _log?.Info($"[设置] 关注名单已更新：{string.Join('、', _watchlist.Entries)}；阈值={_watchlistConfig.MatchThreshold:0.##}");
        BuildRecognitionText(DateTimeOffset.Now, _ppOcr?.Name ?? _ocr.Name);
    }

    private IReadOnlyList<string> SnapshotRecentNames()
    {
        lock (_recentNamesGate)
        {
            return CompactRecentNames();
        }
    }

    /// <summary>显示/隐藏穿透叠加，并持久化。</summary>
    private void ToggleOverlay()
    {
        if (_overlay is null)
        {
            return;
        }

        if (_overlay.IsVisible)
        {
            _overlay.ExitDragMode();
            _overlay.Hide();
            _config.Overlay.Shown = false;
            _notice = "叠加已隐藏。";
        }
        else
        {
            _overlay.Show();
            _config.Overlay.Shown = true;
            _notice = "叠加已显示。";
        }

        ConfigStore.Save(_paths.ConfigFile, _config);
    }

    /// <summary>进入/退出拖动模式；退出时把窗口位置持久化。</summary>
    private void ToggleOverlayDragMode()
    {
        if (_overlay is null || !_overlay.IsVisible)
        {
            _notice = "叠加未显示，无法进入拖动模式。";
            return;
        }

        bool dragging = _overlay.ToggleDragMode();

        if (!dragging)
        {
            _config.Overlay.X = _overlay.Left;
            _config.Overlay.Y = _overlay.Top;
            ConfigStore.Save(_paths.ConfigFile, _config);
            _notice = $"叠加位置已保存：({_overlay.Left:0}, {_overlay.Top:0})。";
            _log?.Info(_notice);
        }
        else
        {
            _notice = "已进入拖动模式：按住左键拖动叠加窗口，Ctrl+Alt+D 退出。";
        }
    }

    private void Quit()
    {
        if (_disposed)
        {
            return;
        }

        Dispose();
        _dispatcher.BeginInvoke(() => Application.Current.Shutdown());
    }

    // ---------- 采集 ----------

    private void CaptureLoop()
    {
        var fpsWatch = Stopwatch.StartNew();
        long fpsFrames = 0;

        while (_running)
        {
            if (_preview?.IsPaused == true)
            {
                Thread.Sleep(100);
                continue;
            }

            PixelRect roi = ResolveRoi();
            if (roi.IsEmpty)
            {
                _notice = $"尚未标定列表区域：按 {HotkeyHint(LabelSelectRoi)} 框选。";
                Thread.Sleep(200);
                PublishFrame();
                continue;
            }

            var watch = Stopwatch.StartNew();
            bool ok = _capture.TryCapture(roi, out ImageFrame? frame, out string? error);
            watch.Stop();

            _lastCaptureMs = watch.Elapsed.TotalMilliseconds;

            if (ok && frame is not null)
            {
                _lastFrame = frame;
                _lastError = string.Empty;
                _frameCount++;
                fpsFrames++;
                AnalyzeAndRecognize(frame);
            }
            else
            {
                _lastError = error ?? "未知错误";
                _log?.Warn($"截屏失败：{_lastError}");
            }

            if (fpsWatch.ElapsedMilliseconds >= 1000)
            {
                _measuredFps = fpsFrames * 1000.0 / fpsWatch.ElapsedMilliseconds;
                fpsWatch.Restart();
                fpsFrames = 0;
            }

            PublishFrame();

            int periodMs = Math.Max(50, 1000 / Math.Max(1, _config.Capture.Fps));
            int sleep = periodMs - (int)watch.ElapsedMilliseconds;
            if (sleep > 0)
            {
                Thread.Sleep(sleep);
            }
        }
    }

    /// <summary>
    /// 被动观测列表结构：每帧做一次行剖分，行数/签名一变就记录，并把变化前后的两帧落盘。
    ///
    /// 为什么要被动：翻页（第 2 页）只有神器总数超过单页上限才出现，**没有视觉信号、也无法定时等待**，
    /// 与其去「找」样本，不如让正常游玩过程自动把它抓下来。
    /// 判据来自实测：第 2 页行数显著少于第 1 页 ⇒「行数骤降 + 内容整体改变」是主信号。
    /// </summary>
    /// <summary>一帧只做一次行剖分，结果同时喂给「列表观测」与「文字识别」，避免重复计算。</summary>
    private void AnalyzeAndRecognize(ImageFrame frame)
    {
        RowProfileReport report;
        try
        {
            report = _rowAnalysisCache.Analyze(frame, image => RowProfileAnalyzer.Analyze(image));
        }
        catch (Exception ex)
        {
            _log?.Error("[分析] 行剖分失败", ex);
            return;
        }

        ObserveListStructure(report, frame);
        Recognize(report, frame);
    }

    /// <summary>
    /// 文字识别：OCR → 行解析 → 跨页跟踪 → 关注名单过滤。
    /// 这一条链路就是 PLAN 第 4 节的「识别层 → 解析层 → 跟踪层」。
    /// </summary>
    private void Recognize(RowProfileReport report, ImageFrame frame)
    {
        var observed = new List<ObservedRow>();
        var unparsed = new List<string>();
        int sourceCount = 0;
        string engineLabel;

        string label = _ppOcr?.Name ?? _ocr.Name;

        // OCR 比采集贵得多（PP-OCR 12 行约 0.8s）。按 PLAN 第 10 节，采集 2Hz、识别低频，
        // 中间靠本地倒计时外推补上；这样既不掉帧，也不必现在去抠 ONNX 性能（留 M6）。
        if (DateTime.UtcNow - _lastOcrAt < OcrInterval)
        {
            BuildRecognitionText(DateTimeOffset.Now, label);
            return;
        }

        _lastOcrAt = DateTime.UtcNow;

        if (_ppOcr is not null)
        {
            IReadOnlyList<RowBand> bands = report.GridBands.Count > 0 ? report.GridBands : report.Bands;
            sourceCount = bands.Count;
            engineLabel = _ppOcr.Name;

            var watch = Stopwatch.StartNew();
            IReadOnlyList<PpOcrRowResult> rows = _ppOcr.RecognizeRows(frame, bands, 3, _ppOcrFixedWidth, _ppOcrBatchSize);
            watch.Stop();
            _lastOcrMs = watch.Elapsed.TotalMilliseconds;

            foreach (PpOcrRowResult row in rows)
            {
                if (row.Confidence < MinRowConfidence)
                {
                    unparsed.Add($"{row.Text}（置信 {row.Confidence:0.00}，过低）");
                    continue;
                }

                ParseInto(row.Text, row.Slot, observed, unparsed);
            }
        }
        else if (_ocr.IsAvailable)
        {
            engineLabel = _ocr.Name;

            var watch = Stopwatch.StartNew();
            bool ok = _ocr.TryRecognize(frame, out IReadOnlyList<OcrTextLine> lines, out string? error);
            watch.Stop();
            _lastOcrMs = watch.Elapsed.TotalMilliseconds;

            if (!ok)
            {
                if (DateTime.UtcNow - _lastOcrWarnAt >= ContentChangeLogMinInterval)
                {
                    _lastOcrWarnAt = DateTime.UtcNow;
                    _log?.Warn($"[识别] OCR 失败：{error}");
                }

                return;
            }

            sourceCount = lines.Count;
            int slot = 0;
            foreach (OcrTextLine line in lines)
            {
                ParseInto(line.Text, slot++, observed, unparsed);
            }
        }
        else
        {
            return;
        }

        // 解析失败的行必须留证据：解析成功率低会让好条目反复被判 miss，是重复追踪的根源之一。
        if (unparsed.Count > 0 && DateTime.UtcNow - _lastUnparsedLogAt >= ContentChangeLogMinInterval)
        {
            _lastUnparsedLogAt = DateTime.UtcNow;
            _log?.Warn($"[识别] 有 {unparsed.Count} 行解析失败，样例：{string.Join(" ｜ ", unparsed.Take(4))}");
        }

        TrackerFrameResult result = _tracker.Observe(report.RowCount, observed, DateTimeOffset.Now);

        if (result.Added.Count > 0 || result.Removed.Count > 0)
        {
            _log?.Info(
                $"[识别] {engineLabel} 页={result.Page} 行数={report.RowCount} 解析={observed.Count}/{sourceCount} "
                + $"新增={result.Added.Count} 移除={result.Removed.Count} 耗时={_lastOcrMs:0}ms");
        }

        DumpTrackerTable();

        BuildRecognitionText(DateTimeOffset.Now, engineLabel);
    }

    /// <summary>
    /// 定期把整张跟踪表打进日志，用于定性「名单条目数不对」到底是真变化还是抖动。
    /// 只记键与状态，不记像素。
    /// </summary>
    private void DumpTrackerTable()
    {
        if (DateTime.UtcNow - _lastEntryDumpAt < EntryDumpInterval)
        {
            return;
        }

        _lastEntryDumpAt = DateTime.UtcNow;

        IReadOnlyList<TrackerEntryView> all = _tracker.Snapshot(DateTimeOffset.Now);
        int matched = all.Count(e => _watchlist.IsEmpty || _watchlist.Match(e.ArtifactName) is not null);

        string detail = string.Join(
            " | ",
            all.Select(e => string.Create(
                CultureInfo.InvariantCulture,
                $"P{(int)e.Page}#{e.Slot}={e.ArtifactName}"
                + $"{(e.State == ArtifactState.Cooling ? $"[{e.CooldownSeconds}]" : "[R]")}"
                + $"{(e.UsesRemaining is { } r && e.UsesTotal is { } t ? $"{r}/{t}" : string.Empty)}"
                + $" {(e.Source == EntrySource.Live ? "实" : "推")}m{e.MissedSessions}")));

        _log?.Info($"[跟踪表] 共 {all.Count} 条（命中名单 {matched} 条）: {detail}");
    }


    /// <summary>把一行识别文本解析成观测；失败时记入 unparsed 备查。玩家名不参与身份，直接丢弃。</summary>
    private void ParseInto(string text, int slot, List<ObservedRow> observed, List<string> unparsed)
    {
        ParsedRow? parsed = StatusLineParser.Parse(text);
        if (parsed is null)
        {
            unparsed.Add(text);
            return;
        }

        lock (_recentNamesGate)
        {
            string name = parsed.ArtifactName.Trim();
            string? existing = _recentNameCounts.Keys
                .Where(candidate => IsRecentAlias(candidate, name))
                .OrderByDescending(candidate => candidate.Length)
                .FirstOrDefault();
            _recentNameCounts[existing ?? name] = _recentNameCounts.GetValueOrDefault(existing ?? name) + 1;
        }
        observed.Add(new ObservedRow(
            slot,
            parsed.ArtifactName,
            parsed.State,
            parsed.CooldownSeconds,
            parsed.UsesRemaining,
            parsed.UsesTotal));
    }

    /// <summary>按关注名单过滤；名单为空时全部显示（否则界面上什么都看不到，没法用）。</summary>
    private void BuildRecognitionText(DateTimeOffset now, string engineLabel)
    {
        IReadOnlyList<TrackerEntryView> entries = _tracker.Snapshot(now);
        Dictionary<string, int> sortRanks = _watchlistConfig.SortOrder
            .Select((name, index) => (name, index))
            .ToDictionary(item => item.name, item => item.index, StringComparer.Ordinal);

        var lines = new List<string>();
        var shown = new List<TrackerEntryView>();

        var visibleEntries = entries
            .Select(entry => (entry, match: _watchlist.Match(entry.ArtifactName)))
            .Where(item => _watchlist.IsEmpty || item.match is not null)
            .OrderBy(item => sortRanks.TryGetValue(item.match?.CanonicalName ?? item.entry.ArtifactName, out int rank) ? rank : int.MaxValue)
            .ThenBy(item => (int)item.entry.Page)
            .ThenBy(item => item.entry.Slot)
            .ToList();

        foreach (var item in visibleEntries)
        {
            TrackerEntryView entry = item.entry;
            WatchlistMatch? match = item.match;

            shown.Add(entry);

            string name = match?.CanonicalName ?? entry.ArtifactName;
            string status = entry.State switch
            {
                ArtifactState.Ready => "[R]",
                ArtifactState.Cooling => $"[{entry.CooldownSeconds}]",
                _ => "[?]",
            };

            if (entry.UsesRemaining is { } remaining && entry.UsesTotal is { } total)
            {
                status += $"{remaining}/{total}";
            }

            string source = entry.Source == EntrySource.Live ? "实时" : "外推";
            lines.Add($"#{(int)entry.Page}-{entry.Slot:00}  {name} {status}   ({source})");
        }

        var sb = new StringBuilder();
        sb.Append("识别结果：");
        sb.Append(entries.Count).Append(" 条在跟踪");

        if (_watchlist.IsEmpty)
        {
            sb.Append("（名单为空 ⇒ 全部显示）");
        }
        else
        {
            sb.Append("，名单 ").Append(_watchlist.Entries.Count).Append(" 项");
        }

        sb.Append("    ").Append(engineLabel).Append(" ").Append(_lastOcrMs.ToString("0", CultureInfo.InvariantCulture)).Append(" ms");

        if (lines.Count == 0)
        {
            sb.AppendLine().Append("（暂无可显示的条目）");
        }
        else
        {
            foreach (string line in lines.Take(14))
            {
                sb.AppendLine().Append("  ").Append(line);
            }
        }

        string[] recentNames;
        lock (_recentNamesGate)
        {
            recentNames = CompactRecentNames().ToArray();
        }

        if (recentNames.Length > 0)
        {
            sb.AppendLine().Append("最近识别到的神器名：").Append(string.Join('、', recentNames));
        }

        _recognitionText = sb.ToString();

        // 叠加窗口是 UI 元素，必须回到 UI 线程更新。
        bool hasWatchlist = !_watchlist.IsEmpty;
        _dispatcher.BeginInvoke(() =>
        {
            _overlay?.UpdateEntries(shown, _config.Overlay, hasWatchlist);
            EnsureOverlayAvoidsRoi();
        });
    }

    private IReadOnlyList<string> CompactRecentNames()
    {
        return _recentNameCounts
            .Where(pair => pair.Value >= 2 || _recentNameCounts.Count <= 8)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(20)
            .Select(pair => pair.Key)
            .ToArray();
    }

    private static bool IsRecentAlias(string left, string right)
    {
        string a = TextSimilarity.Normalize(left);
        string b = TextSimilarity.Normalize(right);
        if (a == b) return true;
        if (a.Length < 3 || b.Length < 3) return false;
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)) return true;
        return Math.Min(a.Length, b.Length) >= 4 && TextSimilarity.Ratio(a, b) >= 0.82;
    }

    /// <summary>
    /// 叠加**绝不能盖住 ROI**：否则截屏会把叠加自己抓进识别区，形成自反馈
    /// （实测踩过：叠加默认位置与 ROI 横向重叠 65px）。
    /// 拖动模式期间不干预，避免和用户抢位置。
    /// </summary>
    private void EnsureOverlayAvoidsRoi()
    {
        if (_overlay is null || !_overlay.IsVisible || _overlay.IsDragMode)
        {
            return;
        }

        PixelRect roi;
        lock (_stateGate)
        {
            roi = _roi;
        }

        double width = _overlay.ActualWidth;
        double height = _overlay.ActualHeight;

        if (roi.IsEmpty || width <= 0 || height <= 0)
        {
            return;
        }

        var overlayRect = new PixelRect((int)_overlay.Left, (int)_overlay.Top, (int)width, (int)height);

        if (overlayRect.Intersect(roi).IsEmpty)
        {
            return;
        }

        int x = roi.X - (int)width - 16;
        int y = (int)_overlay.Top;

        if (x < 0)
        {
            // 左边放不下就放到 ROI 下方
            x = Math.Clamp(roi.X, 0, Math.Max(0, 1920 - (int)width));
            y = roi.Bottom + 16;
        }

        _overlay.SetPosition(x, y);
        _config.Overlay.X = x;
        _config.Overlay.Y = y;
        ConfigStore.Save(_paths.ConfigFile, _config);

        if (DateTime.UtcNow - _lastOverlayGuardAt >= ContentChangeLogMinInterval)
        {
            _lastOverlayGuardAt = DateTime.UtcNow;
            _notice = $"叠加与识别区重叠，已自动移到 ({x},{y})。";
            _log?.Warn("[叠加] " + _notice);
        }
    }

    private void ObserveListStructure(RowProfileReport report, ImageFrame frame)
    {
        _rowCount = report.RowCount;
        _rowPitch = report.Grid?.Pitch ?? report.MedianPitch;

        // 超过单页上限 ⇒ 量测不可信（ROI 混进了非列表内容）。
        // 这种观测绝不能喂给结构跟踪器，否则会把「基准行数」污染成错误量级。
        if (report.RowCount > ListRules.MaxRowsPerPage)
        {
            if (DateTime.UtcNow - _lastOverflowWarnAt >= FollowWarnMinInterval)
            {
                _lastOverflowWarnAt = DateTime.UtcNow;
                _log?.Warn($"[列表观测] 稳健行数 {report.RowCount} 超过单页上限 {ListRules.MaxRowsPerPage}，本次观测已丢弃。");
            }

            _previousFrame = frame;
            return;
        }

        // 签名只取**网格内**的行带：离群行带（场景物件、窗口边缘）会让签名随机抖动。
        string signature = ListStructureSnapshot.BuildSignature(report.GridBands);

        // 注意：必须在 Observe 之前取 HasBaseline——Observe 会建立基准，
        // 之后再判断就永远为真，初始观测会被静默吞掉。
        bool hadBaseline = _structureTracker.HasBaseline;
        ListChange? change = _structureTracker.Observe(
            new ListStructureSnapshot(report.RowCount, _rowPitch, signature));

        if (!hadBaseline)
        {
            _previousFrame = frame;
            _log?.Info(
                $"[列表观测] 初始：稳健行数={report.RowCount} 检出={report.Bands.Count} 离群={report.OutlierBands.Count} "
                + $"行距={report.Grid?.Pitch:0.##} 横向区域={report.RegionCount}");
            return;
        }

        if (change is null)
        {
            _previousFrame = frame;
            return;
        }

        string kind = change.Kind switch
        {
            ListChangeKind.RowCountChanged => $"行数 {change.PreviousRowCount} → {change.CurrentRowCount}",
            ListChangeKind.ContentChanged => "行内容变化",
            _ => $"行数与内容同时变化（{change.PreviousRowCount} → {change.CurrentRowCount}）",
        };

        if (change.LooksLikePageChange)
        {
            kind += $"（疑似翻页：显著少于基准行数 {_structureTracker.BaselineRowCount}）";
        }

        // 纯内容变化只做低频记录：翻页的主信号是**行数骤降**，
        // 而内容签名在场景/视频等非列表内容上会频繁抖动，全量落盘只会刷爆日志与磁盘。
        if (change.Kind == ListChangeKind.ContentChanged)
        {
            _previousFrame = frame;

            if (DateTime.UtcNow - _lastContentChangeLogAt >= ContentChangeLogMinInterval)
            {
                _lastContentChangeLogAt = DateTime.UtcNow;
                _log?.Info($"[列表观测] {kind}（行数不变 {change.CurrentRowCount}）；行距={report.Grid?.Pitch:0.##}");
            }

            return;
        }

        _log?.Info($"[列表观测] {kind}；行距={report.Grid?.Pitch:0.##}；签名={signature}");

        SaveChangePair(kind);
        _previousFrame = frame;
    }

    /// <summary>把变化前后两帧落盘，限流避免场景抖动把磁盘刷爆；容量由独立目录自己滚动管理。</summary>
    private void SaveChangePair(string kind)
    {
        if (DateTime.UtcNow - _lastChangeSaveAt < ChangeSaveMinInterval
            || _changeShots is null
            || _previousFrame is null
            || _lastFrame is null)
        {
            return;
        }

        try
        {
            _changeShots.Save(_previousFrame, "before");
            string saved = _changeShots.Save(_lastFrame, "after");
            _changeSaveCount++;
            _lastChangeSaveAt = DateTime.UtcNow;
            _notice = $"检测到列表变化（{kind}），已存前后两帧：{Path.GetFileName(saved)}";
        }
        catch (Exception ex)
        {
            _log?.Error("[列表观测] 保存变化帧失败", ex);
        }
    }

    /// <summary>按 PLAN 第 9 节自动跟随目标窗口；找不到窗口则沿用上次 ROI。</summary>
    private PixelRect ResolveRoi()
    {
        PixelRect current;
        lock (_stateGate)
        {
            current = _roi;
        }

        RoiConfig cfg = _config.Roi;
        bool hasTarget = !string.IsNullOrWhiteSpace(cfg.TargetWindowProcess)
            || !string.IsNullOrWhiteSpace(cfg.TargetWindowTitle);
        if (!hasTarget)
        {
            return current;
        }

        RelativeRect relative = RelativeRect.Parse(cfg.WindowRelative);
        if (relative.IsEmpty)
        {
            return current;
        }

        WindowInfo? window = WindowLocator.Find(cfg.TargetWindowProcess, cfg.TargetWindowTitle);
        if (window is null)
        {
            _followSummary = "目标窗口未找到，沿用上次 ROI。";
            return current;
        }

        _followSummary = WindowLocator.Describe(window);

        PixelRect mapped = relative.ToAbsolute(window.Bounds);
        if (mapped.IsEmpty)
        {
            return current;
        }

        // 跟随结果必须基本落在显示器内。目标窗口被移到屏幕外、或相对比例失真时，
        // 否则会算出一个屏幕外的矩形并一直照着它抓帧（实测踩过：跟随目标误设为自己）。
        MonitorInfo monitor = MonitorService.FromPoint(mapped.X + mapped.Width / 2, mapped.Y + mapped.Height / 2);
        if (mapped.Intersect(monitor.Bounds).Area < mapped.Area / 2)
        {
            if (DateTime.UtcNow - _lastFollowWarnAt >= FollowWarnMinInterval)
            {
                _lastFollowWarnAt = DateTime.UtcNow;
                _followSummary = $"跟随结果 {mapped} 大部分在屏幕外，已忽略（沿用上次 ROI）。";
                _log?.Warn("[跟随] " + _followSummary);
            }

            return current;
        }

        lock (_stateGate)
        {
            _roi = mapped;
        }

        return mapped;
    }

    private void PublishFrame()
    {
        if (_disposed)
        {
            return;
        }

        BitmapSource? image = BitmapPresenter.ToBitmapSource(_lastFrame);
        string status = BuildStatus();
        string recognition = _recognitionText;
        _dispatcher.BeginInvoke(() => _preview?.ShowFrame(image, status, recognition));
    }

    private string BuildStatus()
    {
        PixelRect roi;
        lock (_stateGate)
        {
            roi = _roi;
        }

        MonitorInfo monitor = MonitorService.Primary();

        var sb = new StringBuilder();
        sb.Append("采集后端: ").Append(_capture.Name);
        sb.Append("    帧数: ").Append(_frameCount);
        sb.Append("    单帧耗时: ").Append(_lastCaptureMs.ToString("0.0", CultureInfo.InvariantCulture)).Append(" ms");
        sb.Append("    实际频率: ").Append(_measuredFps.ToString("0.00", CultureInfo.InvariantCulture)).Append(" Hz");
        sb.AppendLine();

        sb.Append("ROI: ").Append(roi.IsEmpty ? "(未标定)" : roi.ToString());
        sb.Append("    显示器: ").Append(monitor.DeviceName)
          .Append(' ').Append(monitor.Bounds.Width).Append('x').Append(monitor.Bounds.Height)
          .Append(" @").Append((monitor.Scale * 100).ToString("0", CultureInfo.InvariantCulture)).Append("% (")
          .Append(monitor.Dpi).Append(" dpi)");
        sb.AppendLine();

        sb.Append("跟随目标: ").Append(_followSummary);
        sb.AppendLine();

        sb.Append("列表结构: 行数=").Append(_rowCount < 0 ? "(未测)" : _rowCount.ToString(CultureInfo.InvariantCulture))
          .Append("    基准行数=").Append(_structureTracker.HasBaseline ? _structureTracker.BaselineRowCount.ToString(CultureInfo.InvariantCulture) : "(未测)")
          .Append("    行距=").Append(_rowPitch is null ? "(未测)" : _rowPitch.Value.ToString("0.##", CultureInfo.InvariantCulture))
          .Append(" px    已存变化对=").Append(_changeSaveCount);
        sb.AppendLine();

        sb.Append("热键: ").Append(DescribeHotkeys());
        sb.AppendLine();

        sb.Append("截图目录: ").Append(_shots?.Directory).Append("  (共 ").Append(_shots?.Count ?? 0).Append(" 张)");
        sb.AppendLine();

        sb.Append("采集状态: ").Append(string.IsNullOrEmpty(_lastError) ? "正常" : "失败 - " + _lastError);
        sb.AppendLine();

        sb.Append("最近消息: ").Append(_notice);

        return sb.ToString();
    }
}
