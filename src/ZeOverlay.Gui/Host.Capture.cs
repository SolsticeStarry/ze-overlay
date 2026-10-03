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
    // Host 的「采集循环 + 预览发布 + 状态栏」部分（partial）。
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

                // 采集线程上的任何异常都不能杀进程：隔离并记日志，下一帧继续。
                try
                {
                    AnalyzeAndRecognize(frame);
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    _log?.Error("[采集] 分析与识别异常（已隔离，进程继续）", ex);
                }
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

            int configuredPeriodMs = Math.Max(50, 1000 / Math.Max(1, _config.Capture.Fps));
            // 连写/带标号服自动提速（默认 300ms）；倒计时靠独立的 200ms tick 外推，
            // 不需要 5Hz 高频采集——5Hz 会让 GPU OCR 与 GDI BitBlt 每 200ms 抢游戏帧。
            int communityPeriodMs = Math.Clamp(_config.Recognition.CommunityIntervalMs, 150, 1000);
            int periodMs = _communityMode ? Math.Min(configuredPeriodMs, communityPeriodMs) : configuredPeriodMs;
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
    private void PublishFrame()
    {
        if (_disposed)
        {
            return;
        }

        // 预览窗口不可见时不必把 ROI 转成 BitmapSource 并上传（5Hz 的纹理上传会跟游戏抢 GPU）。
        BitmapSource? image = _preview?.IsVisible == true ? Presenter.ToBitmapSource(_lastFrame) : null;
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

        sb.Append("行语法: 自动判断（括号 / 连写）    名表: ").Append(_config.Vocabulary.Count).Append(" 项");
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
