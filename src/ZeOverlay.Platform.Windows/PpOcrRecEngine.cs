using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ZeOverlay.Core.Analysis;
using ZeOverlay.Core.Imaging;

namespace ZeOverlay.Platform.Windows;

public sealed record PpOcrRowResult(int Slot, int Top, int Bottom, string Text, double Confidence);

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

    public PpOcrRecEngine(string modelPath, string keysPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(keysPath);

        _session = new InferenceSession(modelPath);
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();

        int classCount = _session.OutputMetadata[_outputName].Dimensions.Length >= 3
            ? _session.OutputMetadata[_outputName].Dimensions[^1]
            : -1;

        _characters = BuildCharacters(keysPath, classCount);
        Name = $"PP-OCRv3-rec({_characters.Length - (classCount == _characters.Length ? 1 : 0)} 类)";
    }

    public string Name { get; }

    /// <summary>按行识别。多行合成一个 batch 送模型——逐行跑一次 12 行要 800ms，批处理能降一个数量级。</summary>
    public IReadOnlyList<PpOcrRowResult> RecognizeRows(ImageFrame frame, IReadOnlyList<RowBand> bands, int padding = 3)
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

        // 实测：批次=6 反而更慢（1303ms vs 逐行 831ms/12 行），因为要按最长行补齐宽度，
        // 短行白算。所以先用逐行；批量优化留到 M6（可以按宽度分组再批）。
        const int BatchSize = 1;

        for (int start = 0; start < crops.Count; start += BatchSize)
        {
            var chunk = new List<ImageFrame>(BatchSize);
            for (int i = start; i < Math.Min(start + BatchSize, crops.Count); i++)
            {
                chunk.Add(crops[i].Image);
            }

            IReadOnlyList<(string Text, double Confidence)> decoded = RecognizeBatch(chunk);

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
    public IReadOnlyList<(string Text, double Confidence)> RecognizeBatch(IReadOnlyList<ImageFrame> crops)
    {
        ArgumentNullException.ThrowIfNull(crops);

        if (crops.Count == 0)
        {
            return [];
        }

        int batch = crops.Count;
        int width = 8;
        var widths = new int[batch];

        for (int i = 0; i < batch; i++)
        {
            widths[i] = Math.Clamp((int)Math.Ceiling(TargetHeight * crops[i].Width / (double)crops[i].Height), 8, MaxWidth);
            width = Math.Max(width, widths[i]);
        }

        var tensor = new DenseTensor<float>([batch, 3, TargetHeight, width]);

        for (int i = 0; i < batch; i++)
        {
            FillInput(crops[i], tensor, i, width);
        }

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
            _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);

        Tensor<float> output = outputs.First().AsTensor<float>();
        var results = new List<(string, double)>(batch);

        for (int i = 0; i < batch; i++)
        {
            results.Add(Decode(output, i));
        }

        return results;
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
    private (string Text, double Confidence) Decode(Tensor<float> output, int batchIndex)
    {
        int timeSteps = output.Dimensions[1];
        int classes = output.Dimensions[2];

        var builder = new StringBuilder();
        int previous = -1;
        double confidenceSum = 0;
        int confidenceCount = 0;

        for (int t = 0; t < timeSteps; t++)
        {
            int best = 0;
            float bestValue = float.MinValue;

            for (int c = 0; c < classes; c++)
            {
                float value = output[batchIndex, t, c];
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

        for (int y = 0; y < TargetHeight; y++)
        {
            double sy = (y + 0.5) * crop.Height / TargetHeight - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(sy), 0, crop.Height - 1);
            int y1 = Math.Min(y0 + 1, crop.Height - 1);
            double fy = sy - Math.Floor(sy);

            for (int x = 0; x < width; x++)
            {
                double sx = (x + 0.5) * crop.Width / width - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(sx), 0, crop.Width - 1);
                int x1 = Math.Min(x0 + 1, crop.Width - 1);
                double fx = sx - Math.Floor(sx);

                int i00 = (y0 * crop.Width + x0) * 4;
                int i01 = (y0 * crop.Width + x1) * 4;
                int i10 = (y1 * crop.Width + x0) * 4;
                int i11 = (y1 * crop.Width + x1) * 4;

                // PP-OCR 用 cv2 读图 ⇒ BGR。ImageFrame 的通道 0..2 正好是 B,G,R。
                for (int c = 0; c < 3; c++)
                {
                    double top = crop.Bgra[i00 + c] * (1 - fx) + crop.Bgra[i01 + c] * fx;
                    double bottom = crop.Bgra[i10 + c] * (1 - fx) + crop.Bgra[i11 + c] * fx;
                    double value = top * (1 - fy) + bottom * fy;
                    tensor[batchIndex, c, y, x] = (float)((value / 255.0 - 0.5) / 0.5);
                }
            }

            // 右侧补 0（归一化后 0 即中灰），与 PP-OCR 的 padding 一致
            for (int x = width; x < targetWidth; x++)
            {
                tensor[batchIndex, 0, y, x] = 0;
                tensor[batchIndex, 1, y, x] = 0;
                tensor[batchIndex, 2, y, x] = 0;
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
