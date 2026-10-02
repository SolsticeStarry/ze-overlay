
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
/// ZeOverlay 离线工具入口（量测 / 回归 / 自检）。GUI 入口在 ZeOverlay.exe。
///
/// 从 WPF 工程里抽出来，好处：控制台工具能直接输出到 stdout，
/// 且可以只发工具、不装 WPF。
/// </summary>
internal static class Program
{
    private const string Usage = """
        ZeOverlay.Cli —— 离线识别工具

          --capture-once [--frames N] [--delay MS]        采集自检（退出码 0/2/3）
          --analyze-image <png> [--roi x,y,w,h] [--glyphs]  行剖分 / 字形切分
          --ocr <png> [--roi ...]                         系统 OCR 原始行
          --recognize <png> [--roi ...] [--ppocr] [--parser bracket|plain]  完整识别链路
          --bench-ocr <png> [--roi ...] [--model <onnx>] [--threads N] [--repeat N]
                            [--no-spin] [--ep cpu|dml] [--fixed-width N] [--batch N]
                            [--dml-device N] [--out <txt>]  识别耗时拆解
        """;

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine(Usage);
            return 2;
        }

        // 无界面采集自检
        if (args.Any(a => a.Equals("--capture-once", StringComparison.OrdinalIgnoreCase)))
        {
            int frames = ParseIntArg(args, "--frames", 3);
            int delay = ParseIntArg(args, "--delay", 250);
            return CaptureOnce.Run(args, frames, delay);
        }

        int roiIndex = Array.FindIndex(args, a => a.Equals("--roi", StringComparison.OrdinalIgnoreCase));
        string? roiText = roiIndex >= 0 && roiIndex + 1 < args.Length ? args[roiIndex + 1] : null;

        int ocrIndex = Array.FindIndex(args, a => a.Equals("--ocr", StringComparison.OrdinalIgnoreCase));
        if (ocrIndex >= 0)
        {
            string ocrPath = ocrIndex + 1 < args.Length ? args[ocrIndex + 1] : string.Empty;
            return OcrDump.Run(ocrPath, roiText);
        }

        int benchIndex = Array.FindIndex(args, a => a.Equals("--bench-ocr", StringComparison.OrdinalIgnoreCase));
        if (benchIndex >= 0)
        {
            string path = benchIndex + 1 < args.Length ? args[benchIndex + 1] : string.Empty;
            string? model = GetStringArg(args, "--model");
            int? threads = GetIntArg(args, "--threads");
            int repeat = ParseIntArg(args, "--repeat", 3);
            string? outFile = GetStringArg(args, "--out");
            bool? allowSpinning = args.Any(a => a.Equals("--no-spin", StringComparison.OrdinalIgnoreCase)) ? false : null;
            OcrExecutionProvider provider = GetStringArg(args, "--ep")?.ToLowerInvariant() is "dml" or "directml"
                ? OcrExecutionProvider.DirectML
                : OcrExecutionProvider.Cpu;
            int dmlDevice = GetIntArg(args, "--dml-device") ?? 0;
            int fixedWidth = GetIntArg(args, "--fixed-width") ?? 0;
            int batch = GetIntArg(args, "--batch") ?? 1;
            return BenchOcr.Run(path, roiText, model, threads, repeat, outFile, allowSpinning, provider, dmlDevice, fixedWidth, batch);
        }

        int recognizeIndex = Array.FindIndex(args, a => a.Equals("--recognize", StringComparison.OrdinalIgnoreCase));
        if (recognizeIndex >= 0)
        {
            string path = recognizeIndex + 1 < args.Length ? args[recognizeIndex + 1] : string.Empty;
            bool usePpOcr = args.Any(a => a.Equals("--ppocr", StringComparison.OrdinalIgnoreCase));
            ParserMode parserMode = ProfileDefaults.ParseMode(GetStringArg(args, "--parser"));
            Paths cliPaths = Paths.ForExecutable();
            WatchlistConfig list = WatchlistStore.Load(cliPaths.WatchlistFile);
            return RecognizeDump.Run(path, roiText, list.Names, list.MatchThreshold, usePpOcr, parserMode);
        }

        int analyzeIndex = Array.FindIndex(args, a => a.Equals("--analyze-image", StringComparison.OrdinalIgnoreCase));
        if (analyzeIndex >= 0)
        {
            string imagePath = analyzeIndex + 1 < args.Length ? args[analyzeIndex + 1] : string.Empty;
            bool withGlyphs = args.Any(a => a.Equals("--glyphs", StringComparison.OrdinalIgnoreCase));
            return AnalyzeImage.Run(imagePath, roiText, withGlyphs);
        }

        Console.WriteLine(Usage);
        return 2;
    }

    private static int ParseIntArg(string[] args, string name, int fallback)
    {
        int index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out int value) ? value : fallback;
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
}
