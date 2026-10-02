using System.Windows;
using System.Windows.Threading;
using ZeOverlay.Core.Config;
using ZeOverlay.Core.Diagnostics;
using ZeOverlay.Platform.Windows;

namespace ZeOverlay.App;

/// <summary>
/// 应用入口。启动后进入预览窗口；设置窗口由预览窗口按需打开。
/// </summary>
public partial class App : Application
{
    private AppHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 无界面自检模式：ZeOverlay.exe --capture-once [--frames N] [--delay MS]
        if (e.Args.Any(a => a.Equals("--capture-once", StringComparison.OrdinalIgnoreCase)))
        {
            int frames = ParseIntArg(e.Args, "--frames", 3);
            int delay = ParseIntArg(e.Args, "--delay", 250);
            int exitCode = CaptureOnce.Run(e.Args, frames, delay);
            Shutdown(exitCode);
            return;
        }

        bool selectRoiOnly = e.Args.Any(a => a.Equals("--select-roi", StringComparison.OrdinalIgnoreCase));

        // 离线量测模式：ZeOverlay.exe --analyze-image <png 路径> [--roi x,y,w,h] [--glyphs]
        int analyzeIndex = Array.FindIndex(e.Args, a => a.Equals("--analyze-image", StringComparison.OrdinalIgnoreCase));
        int ocrIndex = Array.FindIndex(e.Args, a => a.Equals("--ocr", StringComparison.OrdinalIgnoreCase));

        int roiIndex = Array.FindIndex(e.Args, a => a.Equals("--roi", StringComparison.OrdinalIgnoreCase));
        string? roiText = roiIndex >= 0 && roiIndex + 1 < e.Args.Length ? e.Args[roiIndex + 1] : null;

        if (ocrIndex >= 0)
        {
            string ocrPath = ocrIndex + 1 < e.Args.Length ? e.Args[ocrIndex + 1] : string.Empty;
            Shutdown(OcrDump.Run(ocrPath, roiText));
            return;
        }

        // M6 性能量测：ZeOverlay.exe --bench-ocr <png> [--roi ...] [--model <onnx>] [--threads N] [--repeat N] [--out <txt>]
        int benchIndex = Array.FindIndex(e.Args, a => a.Equals("--bench-ocr", StringComparison.OrdinalIgnoreCase));
        if (benchIndex >= 0)
        {
            string path = benchIndex + 1 < e.Args.Length ? e.Args[benchIndex + 1] : string.Empty;
            string? model = GetStringArg(e.Args, "--model");
            int? threads = GetIntArg(e.Args, "--threads");
            int repeat = ParseIntArg(e.Args, "--repeat", 3);
            string? outFile = GetStringArg(e.Args, "--out");
            bool? allowSpinning = e.Args.Any(a => a.Equals("--no-spin", StringComparison.OrdinalIgnoreCase)) ? false : null;
            OcrExecutionProvider provider = GetStringArg(e.Args, "--ep")?.ToLowerInvariant() is "dml" or "directml"
                ? OcrExecutionProvider.DirectML
                : OcrExecutionProvider.Cpu;
            int dmlDevice = GetIntArg(e.Args, "--dml-device") ?? 0;
            int fixedWidth = GetIntArg(e.Args, "--fixed-width") ?? 0;
            int batch = GetIntArg(e.Args, "--batch") ?? 1;
            Shutdown(BenchOcr.Run(path, roiText, model, threads, repeat, outFile, allowSpinning, provider, dmlDevice, fixedWidth, batch));
            return;
        }

        // 离线跑完整识别链路：ZeOverlay.exe --recognize <png> [--roi x,y,w,h]
        int recognizeIndex = Array.FindIndex(e.Args, a => a.Equals("--recognize", StringComparison.OrdinalIgnoreCase));
        if (recognizeIndex >= 0)
        {
            string path = recognizeIndex + 1 < e.Args.Length ? e.Args[recognizeIndex + 1] : string.Empty;
            bool usePpOcr = e.Args.Any(a => a.Equals("--ppocr", StringComparison.OrdinalIgnoreCase));
            AppPaths cliPaths = AppPaths.ForExecutable();
            WatchlistConfig list = WatchlistStore.Load(cliPaths.WatchlistFile);
            Shutdown(RecognizeDump.Run(path, roiText, list.Names, list.MatchThreshold, usePpOcr));
            return;
        }

        if (analyzeIndex >= 0)
        {
            string imagePath = analyzeIndex + 1 < e.Args.Length ? e.Args[analyzeIndex + 1] : string.Empty;
            bool withGlyphs = e.Args.Any(a => a.Equals("--glyphs", StringComparison.OrdinalIgnoreCase));
            Shutdown(AnalyzeImage.Run(imagePath, roiText, withGlyphs));
            return;
        }

        AppPaths paths = AppPaths.ForExecutable();
        paths.EnsureDirectories();

        using var log = new RollingFileLog(paths.LogsDirectory);
        log.Info($"启动 ZeOverlay M0；基目录={paths.BaseDirectory}");

        DispatcherUnhandledException += (_, args) =>
        {
            log.Error("UI 线程未处理异常", args.Exception);
            MessageBox.Show(args.Exception.Message, "ZeOverlay 出错", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                log.Error("后台线程未处理异常", ex);
            }
        };

        _host = new AppHost(paths, Dispatcher, selectRoiOnly);
        _host.Start();
    }

    private static int ParseIntArg(string[] args, string name, int fallback)
    {
        int index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out int value))
        {
            return value;
        }

        return fallback;
    }

    private static string? GetStringArg(string[] args, string name)
    {
        int index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int? GetIntArg(string[] args, string name)
    {
        int index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out int value) ? value : null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}
