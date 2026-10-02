using System.Diagnostics;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using ZeOverlay.Shared;

namespace ZeOverlay.Stage.Recognition;

public sealed record PpOcrRow(int Slot, int Top, int Bottom, string Text, double Confidence);

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
public sealed class PpOcrEngine : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly string[] _characters;

    public PpOcrEngine(
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

    /// <summary>识别一批已裁好的行（S2 的产物），Slot 原样带回。</summary>
    public IReadOnlyList<PpOcrRow> RecognizeCrops(IReadOnlyList<RowCrop> crops, int fixedWidth = 0, int batchSize = 1)
    {
        var results = new List<PpOcrRow>(crops.Count);
        int step = Math.Max(1, batchSize);

        for (int start = 0; start < crops.Count; start += step)
        {
            var chunk = new List<ImageFrame>(step);
            for (int i = start; i < Math.Min(start + step, crops.Count); i++)
            {
                chunk.Add(crops[i].Image);
            }

            IReadOnlyList<(string Text, double Confidence)> decoded = RecognizeBatch(chunk, fixedWidth);

            for (int i = 0; i < chunk.Count; i++)
            {
                results.Add(new PpOcrRow(
                    crops[start + i].Slot,
                    crops[start + i].Band.Top,
                    crops[start + i].Band.Bottom,
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
        int width = fixedWidth > 0 ? Math.Clamp(fixedWidth, 8, PpOcrInput.MaxWidth) : 8;

        for (int i = 0; i < batch; i++)
        {
            int natural = PpOcrInput.NaturalWidth(crops[i].Width, crops[i].Height);
            if (fixedWidth <= 0)
            {
                width = Math.Max(width, natural);
            }
        }

        var tensor = new DenseTensor<float>([batch, 3, PpOcrInput.TargetHeight, width]);

        var fillWatch = Stopwatch.StartNew();
        for (int i = 0; i < batch; i++)
        {
            PpOcrInput.Fill(crops[i], tensor.Buffer.Span, i, width);
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

        return Decoder.Decode(sample, timeSteps, classes, _characters);
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
