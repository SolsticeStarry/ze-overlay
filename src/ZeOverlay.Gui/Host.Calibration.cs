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

public sealed partial class Host
{
    // Host 的「ROI 标定 / 自动跟随」部分（partial）。
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

        _preview?.ShowFrame(Presenter.ToBitmapSource(_lastFrame), BuildStatus(), _recognitionText);
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
            RowReport report = Analyzer.Analyze(frame);
            _log?.Info(
                $"[ROI 校验] 稳健行数={report.RowCount} 检出={report.Bands.Count} 离群={report.OutlierBands.Count} "
                + $"行距={report.Grid?.Pitch:0.##} 横向区域={report.RegionCount}");

            var warnings = new List<string>();
            if (report.RegionWarning is not null)
            {
                warnings.Add(report.RegionWarning);
            }

            warnings.AddRange(ListRules.ValidateRoi(report.RowCount, report.Grid?.Pitch, roi.Height, MaxObservableRows));

            return warnings.Count == 0 ? null : string.Join("　", warnings);
        }
        catch (Exception ex)
        {
            _log?.Error("[ROI 校验] 分析失败", ex);
            return null;
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

}
