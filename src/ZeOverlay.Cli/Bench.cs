using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

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
/// M6 性能量测：离线重复跑 PP-OCR 识别链路，拆出各阶段耗时 + 进程 CPU 时间。
///
/// 用法：ZeOverlay.exe --bench-ocr &lt;png&gt; [--roi x,y,w,h] [--model &lt;onnx&gt;]
///                        [--threads N] [--repeat N] [--out &lt;txt&gt;]
///
/// 之所以要拆：App 里报「12 行约 0.8s」，而裸 ONNX Runtime 单行只要 ~10ms，
/// 差 5~7 倍，必须定位是预处理、会话配置还是解码拖的。
///
/// 报告写入文件（WinExe 无控制台）。
/// 退出码：0 成功；4 输入/ROI 非法；5 模型缺失或加载失败。
/// </summary>
internal static class BenchOcr
{
    public static int Run(string imagePath, string? roiText, string? modelPath, string? keysFile, int? threads, int repeat, string? outPath, bool? allowSpinning, OcrExecutionProvider provider, int deviceId, int fixedWidth, int batchSize)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            Console.Error.WriteLine($"输入不存在：{imagePath}");
            return 4;
        }

        ImageFrame full;
        try
        {
            full = BitmapCodec.LoadPng(imagePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"读取失败：{ex.Message}");
            return 4;
        }

        PixelRect roi;
        try
        {
            roi = PixelRect.Parse(roiText);
        }
        catch (FormatException)
        {
            return 4;
        }

        if (roi.IsEmpty || roi.Width > full.Width || roi.Height > full.Height)
        {
            roi = new PixelRect(0, 0, full.Width, full.Height);
        }

        ImageFrame target = roi.X == 0 && roi.Y == 0 && roi.Width == full.Width && roi.Height == full.Height
            ? full
            : full.Crop(roi.X, roi.Y, roi.Width, roi.Height);

        Paths paths = Paths.ForExecutable();
        PpOcrModels.Resolved resolved = PpOcrModels.Resolve(
            Path.Combine(paths.BaseDirectory, "models"), modelPath, keysFile);
        string resolvedModel = resolved.ModelPath;
        string keysPath = resolved.KeysPath;

        if (!File.Exists(resolvedModel) || !File.Exists(keysPath))
        {
            Console.Error.WriteLine($"模型/字典缺失：{resolvedModel} | {keysPath}");
            return 5;
        }

        using var engine = new PpOcrEngine(resolvedModel, keysPath, threads, allowSpinning, provider, deviceId, label: resolved.Label);
        RowReport report = Analyzer.Analyze(target);
        IReadOnlyList<RowBand> bands = report.GridBands.Count > 0 ? report.GridBands : report.Bands;
        IReadOnlyList<RowCrop> crops = new GlyphSegmentationStage().Process(new RowInput(target, report));

        var sb = new StringBuilder();
        sb.AppendLine("=== PP-OCR bench ===");
        sb.AppendLine($"图像   : {imagePath}  ({full.Width}x{full.Height})");
        sb.AppendLine($"ROI    : {roi}  分析区 {target.Width}x{target.Height}");
        sb.AppendLine($"引擎   : {engine.Name}");
        sb.AppendLine($"模型   : {resolvedModel}  ({new FileInfo(resolvedModel).Length / 1048576.0:0.00} MB)   字典: {Path.GetFileName(keysPath)}");
        sb.AppendLine($"EP     : {provider}   线程: {(threads is { } t ? t.ToString(CultureInfo.InvariantCulture) : "自动")}   自旋: {(allowSpinning is { } s ? (s ? "开" : "关") : "默认")}   固定宽度: {(fixedWidth > 0 ? fixedWidth.ToString(CultureInfo.InvariantCulture) : "关")}   批大小: {Math.Max(1, batchSize)}");
        sb.AppendLine($"行数   : {bands.Count}");

        if (bands.Count == 0)
        {
            sb.AppendLine("（没有行，无法量测）");
            WriteOut(outPath, imagePath, sb.ToString());
            return 0;
        }

        // 预热：首次 Run 要建线程池 / cuDNN 式算法选择，不计入。
        _ = engine.RecognizeCrops(crops, fixedWidth, Math.Max(1, batchSize));

        int runs = Math.Max(1, repeat);
        var wall = new List<double>(runs);
        IReadOnlyList<PpOcrRow>? last = null;

        // CPU 时间按「整轮累计 / 次数」取，避免单次采样受 15ms 粒度影响。
        TimeSpan cpuAll0 = Process.GetCurrentProcess().TotalProcessorTime;
        for (int i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            last = engine.RecognizeCrops(crops, fixedWidth, Math.Max(1, batchSize));
            sw.Stop();
            wall.Add(sw.Elapsed.TotalMilliseconds);
        }

        TimeSpan cpuAll1 = Process.GetCurrentProcess().TotalProcessorTime;
        double cpuMedian = (cpuAll1 - cpuAll0).TotalMilliseconds / runs;

        // 空闲 CPU：识别之外，ORT 工作线程是否还在自旋。1Hz 场景下这块常常比识别本身更耗 CPU。
        TimeSpan idleCpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        long idleWall0 = Environment.TickCount64;
        Thread.Sleep(2000);
        TimeSpan idleCpu1 = Process.GetCurrentProcess().TotalProcessorTime;
        long idleWallMs = Environment.TickCount64 - idleWall0;
        double idleCpuMs = (idleCpu1 - idleCpu0).TotalMilliseconds;

        // 校准：单线程忙等固定时长，验证 CPU 计时是否可信（防止拿错误的数字下结论）。
        TimeSpan cal0 = Process.GetCurrentProcess().TotalProcessorTime;
        var calWatch = Stopwatch.StartNew();
        double sink = 0;
        while (calWatch.ElapsedMilliseconds < 300)
        {
            sink += Math.Sqrt(calWatch.ElapsedTicks + 1);
        }

        calWatch.Stop();
        TimeSpan cal1 = Process.GetCurrentProcess().TotalProcessorTime;
        double calCpuMs = (cal1 - cal0).TotalMilliseconds;
        GC.KeepAlive(sink);

        // 阶段拆解（最后一次配置下，单独各测 3 次取中位）：
        //   1) 行分析      Analyzer.Analyze
        //   2) 字形切分    Segmenter.SegmentRow（RecognizeRows 内部逐行做）
        //   3) 模型+解码   RecognizeCrop（含 FillInput 预处理，但不含字形切分）
        double analyzeMs = MedianOf(3, () => { Analyzer.Analyze(target); return 0; });
        double glyphMs = MedianOf(3, () =>
        {
            double acc = 0;
            foreach (RowBand b in bands)
            {
                var g = Segmenter.SegmentRow(target, b.Top, b.Bottom);
                acc += g.Glyphs.Count;
            }

            return acc;
        });

        var inferCrops = new List<ImageFrame>(bands.Count);
        foreach (RowBand b in bands)
        {
            int top = Math.Max(0, b.Top - 3);
            int bottom = Math.Min(target.Height - 1, b.Bottom + 3);
            if (bottom > top)
            {
                inferCrops.Add(target.Crop(b.Left, top, b.Right - b.Left + 1, bottom - top + 1));
            }
        }

        // RecognizeCrop 内部三段：FillInput 预处理 / ONNX Run / CTC 解码。逐行累加。
        var fillSamples = new List<double>();
        var runSamples = new List<double>();
        var decodeSamples = new List<double>();
        var widthsSeen = new List<int>();
        double inferMs = 0;

        for (int pass = 0; pass < 3; pass++)
        {
            double fill = 0, run = 0, decode = 0;
            var sw = Stopwatch.StartNew();
            foreach (ImageFrame c in inferCrops)
            {
                PpOcrEngine.BatchTiming bt = engine.RecognizeBatchTimed([c], fixedWidth);
                fill += bt.FillInputMs;
                run += bt.RunMs;
                decode += bt.DecodeMs;
            }

            sw.Stop();
            inferMs = Math.Max(inferMs, sw.Elapsed.TotalMilliseconds);
            fillSamples.Add(fill);
            runSamples.Add(run);
            decodeSamples.Add(decode);
            if (widthsSeen.Count == 0)
            {
                foreach (ImageFrame c in inferCrops)
                {
                    widthsSeen.Add(Math.Clamp((int)Math.Ceiling(48 * c.Width / (double)c.Height), 8, 1200));
                }
            }
        }

        double fullMedian = Median(wall);

        sb.AppendLine();
        sb.AppendLine("--- 整链路 RecognizeRows（含行分析外的一切）---");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"墙钟 : min={wall.Min():0.0}  中位={fullMedian:0.0}  mean={wall.Average():0.0}  max={wall.Max():0.0} ms   （{runs} 次）"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"CPU  : 平均每次={cpuMedian:0.0} ms   等效核数≈{cpuMedian / Math.Max(1e-6, fullMedian):0.00}   每行墙钟≈{fullMedian / bands.Count:0.0} ms"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"空闲 : CPU={idleCpuMs:0.0} ms / {idleWallMs} ms ≈ {idleCpuMs / Math.Max(1, idleWallMs):0.00} 核（识别间隙的自旋/空闲占用）"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"校准 : 单线程忙等 {calWatch.Elapsed.TotalMilliseconds:0} ms → CPU 计 {calCpuMs:0} ms（≈100% 才可信）"));
        sb.AppendLine();
        sb.AppendLine("--- 阶段拆解（中位）---");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"行分析 Analyzer : {analyzeMs:0.0} ms"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"字形切分 Segmenter  : {glyphMs:0.0} ms（{bands.Count} 行）"));
        double fillMed = Median(fillSamples);
        double runMed = Median(runSamples);
        double decodeMed = Median(decodeSamples);
        string widthList = inferCrops.Count > 0
            ? $"  宽度 {widthsSeen.Min()}~{widthsSeen.Max()}（12 行按 48 高换算）"
            : string.Empty;

        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"模型链路 RecognizeCrop  : {inferMs:0.0} ms（{inferCrops.Count} 行）{widthList}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    ├ FillInput 预处理   : {fillMed:0.0} ms"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    ├ ONNX Run 推理      : {runMed:0.0} ms"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    └ CTC Decode 解码    : {decodeMed:0.0} ms"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"其余（裁剪/编排/分配）    : {Math.Max(0, fullMedian - analyzeMs - glyphMs - inferMs):0.0} ms（估计）"));
        sb.AppendLine();

        if (last is not null)
        {
            sb.AppendLine("--- 识别结果（最后一次）---");
            foreach (PpOcrRow row in last.OrderBy(r => r.Slot))
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"  槽{row.Slot,2} 置信{row.Confidence:0.00}  {row.Text}"));
            }
        }

        WriteOut(outPath, imagePath, sb.ToString());
        return 0;
    }

    private static void WriteOut(string? outPath, string imagePath, string text)
    {
        string path = string.IsNullOrWhiteSpace(outPath)
            ? Path.ChangeExtension(imagePath, null) + ".bench.txt"
            : outPath;
        File.WriteAllText(path, text);
        Console.WriteLine(text);
        Console.WriteLine($"报告已写入：{path}");
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>跑 <paramref name="times"/> 次取中位墙钟；<paramref name="action"/> 返回值用于防止空循环被优化掉。</summary>
    private static double MedianOf(double times, Func<double> action)
    {
        if (times <= 1)
        {
            var sw = Stopwatch.StartNew();
            _ = action();
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }

        var samples = new List<double>((int)times);
        for (int i = 0; i < times; i++)
        {
            var sw = Stopwatch.StartNew();
            _ = action();
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        return Median(samples);
    }
}
