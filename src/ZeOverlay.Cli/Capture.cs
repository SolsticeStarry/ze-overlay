using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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

namespace ZeOverlay.Cli;

/// <summary>
/// 无界面自检：按 config.json 的 ROI 连抓若干帧，落盘一张 PNG 与一份 JSON 报告后退出。
/// 用途：M0 验收「能否稳定抓到列表区域」，以及后续真机回归的自动化入口。
/// 退出码：0 = 全部成功；2 = 未标定；3 = 存在失败帧。
/// </summary>
internal static class CaptureOnce
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Run(string[] args, int frames, int delayMs)
    {
        Paths paths = Paths.ForExecutable();
        AppConfig config = ConfigStore.Load(paths.ConfigFile);
        paths.ShotsDirName = config.Storage.ShotsDirName;
        paths.LogsDirName = config.Storage.LogsDirName;
        paths.EnsureDirectories();

        using var log = new Log(paths.LogsDirectory, "app.log", config.Storage.MaxLogBytes, config.Storage.MaxLogFiles);
        var shots = new Shots(paths.ShotsDirectory, config.Storage.MaxShots);
        using IScreenCapture capture = new Gdi();

        string reportPath = Path.Combine(paths.ShotsDirectory, "capture-report.json");

        if (!config.Roi.IsSet)
        {
            WriteReport(reportPath, new CaptureReport
            {
                Ok = false,
                Reason = "config.json 中尚未标定 ROI（Roi.IsSet=false）。",
            });
            log.Warn("capture-once：未标定 ROI。");
            return 2;
        }

        PixelRect roi = PixelRect.Parse(config.Roi.ScreenRect);
        if (roi.IsEmpty)
        {
            WriteReport(reportPath, new CaptureReport
            {
                Ok = false,
                Reason = $"ROI 解析为空：'{config.Roi.ScreenRect}'。",
            });
            log.Warn("capture-once：ROI 为空。");
            return 2;
        }

        var report = new CaptureReport
        {
            Ok = true,
            Backend = capture.Name,
            Roi = roi.ToString(),
            Monitor = MonitorService.FromPoint(roi.X + roi.Width / 2, roi.Y + roi.Height / 2).Bounds.ToString(),
        };

        string? savedPng = null;

        for (int i = 0; i < Math.Max(1, frames); i++)
        {
            var watch = Stopwatch.StartNew();
            bool ok = capture.TryCapture(roi, out ImageFrame? frame, out string? error);
            watch.Stop();

            var sample = new CaptureSample
            {
                Index = i,
                Ok = ok && frame is not null,
                Milliseconds = Math.Round(watch.Elapsed.TotalMilliseconds, 2),
                Error = error,
            };

            if (frame is not null)
            {
                sample.Width = frame.Width;
                sample.Height = frame.Height;
            }

            report.Frames.Add(sample);

            if (sample.Ok && savedPng is null && frame is not null)
            {
                savedPng = shots.Save(frame, "selftest");
                report.SavedPng = savedPng;
                report.MeanLuminance = MeanLuminance(frame);
            }

            if (i < frames - 1)
            {
                Thread.Sleep(Math.Max(0, delayMs));
            }
        }

        report.Ok = report.Frames.TrueForAll(f => f.Ok);
        report.SuccessCount = report.Frames.Count(f => f.Ok);

        WriteReport(reportPath, report);
        log.Info($"capture-once：{report.SuccessCount}/{report.Frames.Count} 帧成功；报告={reportPath}；截图={savedPng}");

        return report.Ok ? 0 : 3;
    }

    /// <summary>平均亮度用于快速判断「是不是全黑帧」（全黑通常是独占全屏/硬件叠加导致）。</summary>
    private static double MeanLuminance(ImageFrame frame)
    {
        long sum = 0;
        long count = (long)frame.Width * frame.Height;

        // 抽样即可，避免 1080p 逐像素过慢。
        for (int y = 0; y < frame.Height; y += 3)
        {
            for (int x = 0; x < frame.Width; x += 3)
            {
                sum += frame.LuminanceAt(x, y);
            }
        }

        long sampled = (long)Math.Ceiling(frame.Width / 3.0) * (long)Math.Ceiling(frame.Height / 3.0);
        return count == 0 || sampled == 0 ? 0 : Math.Round(sum / (double)sampled, 2);
    }

    private static void WriteReport(string path, CaptureReport report)
    {
        report.Timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));
    }

    private sealed class CaptureReport
    {
        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = string.Empty;

        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        [JsonPropertyName("backend")]
        public string? Backend { get; set; }

        [JsonPropertyName("roi")]
        public string? Roi { get; set; }

        [JsonPropertyName("monitor")]
        public string? Monitor { get; set; }

        [JsonPropertyName("successCount")]
        public int SuccessCount { get; set; }

        [JsonPropertyName("savedPng")]
        public string? SavedPng { get; set; }

        [JsonPropertyName("meanLuminance")]
        public double MeanLuminance { get; set; }

        [JsonPropertyName("frames")]
        public List<CaptureSample> Frames { get; } = [];
    }

    private sealed class CaptureSample
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("ms")]
        public double Milliseconds { get; set; }

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}
