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
    // Host 的「截图 / 预览 / 叠加开关 / 设置 / 退出」部分（partial）。
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

    /// <summary>用户在左侧设置面板点「应用设置」后，落盘并即时生效。</summary>
    private void ApplySettingsFromPanel()
    {
        if (_disposed || _preview is null)
        {
            return;
        }

        AppConfig updated = _preview.Settings.Configuration;
        WatchlistConfig updatedWatchlist = _preview.Settings.Watchlist;

        // 未标定的字段（ROI/存储/单显示器）沿用原值；显示/频率/热键/跟踪整体替换。
        _config.Overlay = updated.Overlay;
        _config.Capture = updated.Capture;
        _config.Hotkeys = updated.Hotkeys;
        _config.Recognition = updated.Recognition;
        _config.Tracking = updated.Tracking;
        _trackerOptions.DisappearAfterSeconds = Math.Clamp(_config.Tracking.RowDisappearSeconds, 0.5, 60);

        _watchlistConfig = updatedWatchlist;
        WatchlistStore.Save(_paths.WatchlistFile, _watchlistConfig);
        ConfigStore.Save(_paths.ConfigFile, _config);

        _watchlist = new Matcher(
            _watchlistConfig.Names,
            new WatchlistOptions { Threshold = _watchlistConfig.MatchThreshold });
        BuildPipeline();
        ReloadHotkeys();
        _notice = $"设置已应用：名单 {_watchlist.Entries.Count} 项，阈值 {_watchlistConfig.MatchThreshold:0.##}，排序 {OverlaySort.Parse(_config.Overlay.SortMode)}";
        _log?.Info($"[设置] 已应用：名单={string.Join('、', _watchlist.Entries)}；阈值={_watchlistConfig.MatchThreshold:0.##}；"
            + $"排序={OverlaySort.Parse(_config.Overlay.SortMode)}；采集={_config.Capture.Fps}Hz；识别间隔={Math.Clamp(_config.Recognition.IntervalMs, 200, 5000)}ms");
        BuildRecognitionText(DateTimeOffset.Now, _recognizer?.Name ?? _ocr.Name);
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

}
