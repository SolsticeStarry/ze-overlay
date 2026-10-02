using System.Diagnostics;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ZeOverlay.Core.Analysis;
using ZeOverlay.Core.Imaging;

namespace ZeOverlay.Platform.Windows;

public sealed record PpOcrRowResult(int Slot, int Top, int Bottom, string Text, double Confidence);

/// <summary>识别后端使用的 ONNX Runtime 执行提供程序。</summary>
public enum OcrExecutionProvider
{
    /// <summary>CPU（默认，跨平台稳定）。</summary>
    Cpu,

    /// <summary>DirectML：任何 DX12 GPU 通用加速（本机 RTX 3050 可用）。</summary>
    DirectML,
}

/// <summary>
/// PP-OCRv3 识别（rec）后端。
///
/// **不接检测（det）模型**：行切分我们已经做好了（等距网格吸附），
/// 直接把每一行裁出来送 rec 即可 —— 省掉 DB 检测的阈值/轮廓/unclip/透视变换一整套后处理。
///
/// 之所以换掉系统 OCR：实测系统 OCR 的可靠性随场景背景剧烈变化
/// （背景较暗时 23/23 行正确；背景是很亮的浅灰墙时 0/6 行、全乱码），
/// 而 PP-OCR 是在大量真实场景上训练的，这正是 `PLAN.md` 第 3.2 节把它列为首选的原因。
/// </summary>
public sealed class PpOcrRecEngine : IDisposable
{
    private const int TargetHeight = 48;
    private const int MaxWidth = 1200;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly string[] _characters;

    public PpOcrRecEngine(
        string modelPath,
        string keysPath,
        int? intraOpThreads = null,
        bool? allowSpinning = null,
        OcrExecutionProvider provider = OcrExecutionProvider.Cpu,
        int deviceId = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(keysPath);

        ModelPath = modelPath;
        Provider = provider;

        bool needsOptions = intraOpThreads is { } || allowSpinning is { } || provider != OcrExecutionProvider.Cpu;

        if (needsOptions)
        {
            // 线程数/自旋只对 CPU EP 生效；DML 下设置它们不会报错，但由 GPU 调度接管。
            using var options = new SessionOptions();

            if (intraOpThreads is { } t)
            {
                options.IntraOpNumThreads = Math.Max(1, t);
                options.InterOpNumThreads = 1;
            }

            if (allowSpinning is { } spin)
            {
                options.AddSessionConfigEntry("session.intra_op.allow_spinning", spin ? "1" : "0");
            }

            if (provider == OcrExecutionProvider.DirectML)
            {
                options.AppendExecutionProvider_DML(deviceId);
            }

            _session = new InferenceSession(modelPath, options);
        }
        else
        {
            _session = new InferenceSession(modelPath);
        }

        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();

        int classCount = _session.OutputMetadata[_outputName].Dimensions.Length >= 3
            ? _session.OutputMetadata[_outputName].Dimensions[^1]
            : -1;

        _characters = BuildCharacters(keysPath, classCount);
        string ep = provider == OcrExecutionProvider.DirectML ? "+DML" : string.Empty;
        Name = $"PP-OCRv3-rec({_characters.Length - (classCount == _characters.Length ? 1 : 0)} 类){ep}";
    }

    public string Name { get; }

    /// <summary>实际使用的执行提供程序（供日志/基准区分）。</summary>
    public OcrExecutionProvider Provider { get; }

    /// <summary>实际加载的模型文件路径（供基准/诊断记录）。</summary>
    public string ModelPath { get; }

    /// <summary>按行识别。<paramref name="fixedInputWidth"/> &gt; 0 时把所有行补到同一宽度（不改长宽比），
    /// 用于让 DirectML 只编译一次算子（换形状会触发重编译，实测拖慢 3 倍）。
    /// <paramref name="batchSize"/> &gt; 1 时把多行合成一个 batch 送模型（DML 上更划算；CPU 上反而更慢）。</summary>
    public IReadOnlyList<PpOcrRowResult> RecognizeRows(ImageFrame frame, IReadOnlyList<RowBand> bands, int padding = 3, int fixedInputWidth = 0, int batchSize = 1)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(bands);

        var crops = new List<(int Slot, int Top, int Bottom, ImageFrame Image)>(bands.Count);
        int slot = 0;

        foreach (RowBand band in bands)
        {
            int top = Math.Max(0, band.Top - padding);
            int bottom = Math.Min(frame.Height - 1, band.Bottom + padding);

            if (bottom <= top)
            {
                continue;
            }

            // 用**字形切分的实际边界**裁剪，而不是行剖分的估计边界。
            // 实测教训：按估计边界右边留了空白时，PP-OCR 会在行末「幻觉」出英文字符
            // （`皇家口粮` → `皇家口粮erdrinked`、`心绘灵` → `心绘灵effect`），
            // 于是同一行每帧都产生新 key ⇒ 重复条目。
            GlyphLine glyphs = GlyphSegmenter.SegmentRow(frame, band.Top, band.Bottom);

            int left;
            int right;

            if (glyphs.Glyphs.Count > 0)
            {
                left = glyphs.Glyphs[0].Left;
                right = glyphs.Glyphs[^1].Right;
            }
            else
            {
                left = band.Left;
                right = band.Right;
            }

            left = Math.Max(0, left - 2);
            right = Math.Min(frame.Width - 1, right + 2);

            int width = right - left + 1;

            if (width < 4)
            {
                continue;
            }

            crops.Add((slot++, band.Top, band.Bottom, frame.Crop(left, top, width, bottom - top + 1)));
        }

        var results = new List<PpOcrRowResult>(crops.Count);

        // CPU 上批处理更慢（要按最长行补齐宽度，短行白算）；DML 上批处理更快
        // （摊薄每次 Run 的固定开销）。默认逐行，由调用方按 EP 决定。
        int step = Math.Max(1, batchSize);

        for (int start = 0; start < crops.Count; start += step)
        {
            var chunk = new List<ImageFrame>(step);
            for (int i = start; i < Math.Min(start + step, crops.Count); i++)
            {
                chunk.Add(crops[i].Image);
            }

            IReadOnlyList<(string Text, double Confidence)> decoded = RecognizeBatch(chunk, fixedInputWidth);

            for (int i = 0; i < chunk.Count; i++)
            {
                results.Add(new PpOcrRowResult(
                    crops[start + i].Slot,
                    crops[start + i].Top,
                    crops[start + i].Bottom,
                    decoded[i].Text,
                    decoded[i].Confidence));
            }
        }

        return results;
    }

    /// <summary>批量识别一批同高裁剪（内部会补齐到同一宽度）。</summary>
    public IReadOnlyList<(string Text, double Confidence)> RecognizeBatch(IReadOnlyList<ImageFrame> crops, int fixedWidth = 0)
        => RecognizeBatchTimed(crops, fixedWidth).Results;

    /// <summary>单批各阶段耗时，供 M6 性能量测使用。</summary>
    public sealed record BatchTiming(
        IReadOnlyList<(string Text, double Confidence)> Results,
        double FillInputMs,
        double RunMs,
        double DecodeMs,
        int Width);

    /// <summary>
    /// 与 <see cref="RecognizeBatch"/> 相同，但把「预处理 / 推理 / 解码」三段分别计时。
    /// 只为量测存在，热路径仍走 <see cref="RecognizeBatch"/>。
    /// </summary>
    public BatchTiming RecognizeBatchTimed(IReadOnlyList<ImageFrame> crops, int fixedWidth = 0)
    {
        ArgumentNullException.ThrowIfNull(crops);

        if (crops.Count == 0)
        {
            return new BatchTiming([], 0, 0, 0, 0);
        }

        int batch = crops.Count;
        int width = fixedWidth > 0 ? Math.Clamp(fixedWidth, 8, MaxWidth) : 8;

        for (int i = 0; i < batch; i++)
        {
            int natural = Math.Clamp((int)Math.Ceiling(TargetHeight * crops[i].Width / (double)crops[i].Height), 8, MaxWidth);
            if (fixedWidth <= 0)
            {
                width = Math.Max(width, natural);
            }
        }

        var tensor = new DenseTensor<float>([batch, 3, TargetHeight, width]);

        var fillWatch = Stopwatch.StartNew();
        for (int i = 0; i < batch; i++)
        {
            FillInput(crops[i], tensor, i, width);
        }

        fillWatch.Stop();

        var runWatch = Stopwatch.StartNew();
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
            _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
        runWatch.Stop();

        Tensor<float> output = outputs.First().AsTensor<float>();
        var denseOutput = (DenseTensor<float>)output;
        var results = new List<(string, double)>(batch);

        var decodeWatch = Stopwatch.StartNew();
        for (int i = 0; i < batch; i++)
        {
            results.Add(Decode(denseOutput, i));
        }

        decodeWatch.Stop();

        return new BatchTiming(
            results,
            fillWatch.Elapsed.TotalMilliseconds,
            runWatch.Elapsed.TotalMilliseconds,
            decodeWatch.Elapsed.TotalMilliseconds,
            width);
    }

    public (string Text, double Confidence) RecognizeCrop(ImageFrame crop)
    {
        IReadOnlyList<(string Text, double Confidence)> batch = RecognizeBatch([crop]);
        return batch.Count > 0 ? batch[0] : (string.Empty, 0);
    }

    public void Dispose() => _session.Dispose();

    private string CharacterAt(int index)
        => index >= 0 && index < _characters.Length ? _characters[index] : string.Empty;

    /// <summary>CTC 贪心解码第 <paramref name="batchIndex"/> 个样本。</summary>
    private (string Text, double Confidence) Decode(DenseTensor<float> output, int batchIndex)
    {
        int timeSteps = output.Dimensions[1];
        int classes = output.Dimensions[2];

        // 关键：不要用 output[i,j,k] 多维索引器逐元素取——实测 12 行要 ~400ms（占全链路 2/3）。
        // 索引器每次访问都要算 stride 并做边界检查；直接扫底层 buffer 的 span 快一个数量级。
        ReadOnlySpan<float> buffer = output.Buffer.Span;
        int offset = batchIndex * timeSteps * classes;
        ReadOnlySpan<float> sample = buffer.Slice(offset, timeSteps * classes);

        var builder = new StringBuilder();
        int previous = -1;
        double confidenceSum = 0;
        int confidenceCount = 0;

        for (int t = 0; t < timeSteps; t++)
        {
            ReadOnlySpan<float> step = sample.Slice(t * classes, classes);
            int best = 0;
            float bestValue = step[0];

            for (int c = 1; c < classes; c++)
            {
                float value = step[c];
                if (value > bestValue)
                {
                    bestValue = value;
                    best = c;
                }
            }

            // CTC：跳过 blank(0) 与重复
            if (best != 0 && best != previous)
            {
                builder.Append(CharacterAt(best));
                confidenceSum += bestValue;
                confidenceCount++;
            }

            previous = best;
        }

        double confidence = confidenceCount == 0 ? 0 : confidenceSum / confidenceCount;
        return (builder.ToString(), confidence);
    }

    /// <summary>把一张裁剪缩放到 48 高、按比例定宽，归一化后写入 batch 的第 <paramref name="batchIndex"/> 项。</summary>
    private static void FillInput(ImageFrame crop, DenseTensor<float> tensor, int batchIndex, int targetWidth)
    {
        double ratio = crop.Width / (double)crop.Height;
        int width = Math.Clamp((int)Math.Ceiling(TargetHeight * ratio), 8, targetWidth);

        // 预计算每个输出列的源列与权重：原来这些除法/取整放在 y 循环内层，
        // 等于对每一行重复算一遍（48 次）。挪出来以后内层只做乘加。
        var x0s = new int[width];
        var x1s = new int[width];
        var fxs = new double[width];
        double scaleX = crop.Width / (double)width;

        for (int x = 0; x < width; x++)
        {
            double sx = (x + 0.5) * scaleX - 0.5;
            double floorX = Math.Floor(sx);
            int x0 = Math.Clamp((int)floorX, 0, crop.Width - 1);
            x0s[x] = x0;
            x1s[x] = Math.Min(x0 + 1, crop.Width - 1);
            fxs[x] = sx - floorX;
        }

        // 直接写底层 buffer：tensor[i,c,y,x] 的三维索引器每次都要算 stride + 边界检查，
        // 48×width×3 次下来是预处理里最贵的一块。
        ReadOnlySpan<byte> src = crop.Bgra;
        Span<float> dst = tensor.Buffer.Span;
        int plane = TargetHeight * targetWidth;
        int dstBase = batchIndex * 3 * plane;
        double scaleY = crop.Height / (double)TargetHeight;

        for (int y = 0; y < TargetHeight; y++)
        {
            double sy = (y + 0.5) * scaleY - 0.5;
            double floorY = Math.Floor(sy);
            int y0 = Math.Clamp((int)floorY, 0, crop.Height - 1);
            int y1 = Math.Min(y0 + 1, crop.Height - 1);
            double fy = sy - floorY;
            double oneMinusFy = 1 - fy;
            int row0 = y0 * crop.Width;
            int row1 = y1 * crop.Width;

            for (int x = 0; x < width; x++)
            {
                int x0 = x0s[x];
                int x1 = x1s[x];
                double fx = fxs[x];
                double oneMinusFx = 1 - fx;

                int i00 = (row0 + x0) * 4;
                int i01 = (row0 + x1) * 4;
                int i10 = (row1 + x0) * 4;
                int i11 = (row1 + x1) * 4;

                // PP-OCR 用 cv2 读图 ⇒ BGR。ImageFrame 的通道 0..2 正好是 B,G,R。
                for (int c = 0; c < 3; c++)
                {
                    double top = src[i00 + c] * oneMinusFx + src[i01 + c] * fx;
                    double bottom = src[i10 + c] * oneMinusFx + src[i11 + c] * fx;
                    double value = top * oneMinusFy + bottom * fy;
                    dst[dstBase + c * plane + y * targetWidth + x] = (float)((value / 255.0 - 0.5) / 0.5);
                }
            }

            // 右侧补 0（归一化后 0 即中灰），与 PP-OCR 的 padding 一致
            for (int c = 0; c < 3; c++)
            {
                int rowBase = dstBase + c * plane + y * targetWidth;
                for (int x = width; x < targetWidth; x++)
                {
                    dst[rowBase + x] = 0;
                }
            }
        }
    }

    /// <summary>
    /// 字典：PP-OCR 的 CTCLabelDecode 用 `['blank'] + keys (+ [' '])`。
    /// 模型输出类别数为 keys.Count+2 时追加空格（`use_space_char=True`）。
    /// </summary>
    private static string[] BuildCharacters(string keysPath, int classCount)
    {
        var keys = new List<string>();
        foreach (string line in File.ReadAllLines(keysPath))
        {
            keys.Add(line.TrimEnd('\r', '\n'));
        }

        var characters = new List<string>(keys.Count + 2) { "blank" };
        characters.AddRange(keys);

        if (classCount < 0 || classCount == keys.Count + 2)
        {
            characters.Add(" ");
        }

        return characters.ToArray();
    }
}
