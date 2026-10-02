using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ZeOverlay.Core.Geometry;
using ZeOverlay.Core.Imaging;
using ZeOverlay.Platform.Windows;

namespace ZeOverlay.App;

/// <summary>
/// 离线跑一次系统 OCR，用于验证「这个游戏面板到底能不能被读出来」。
/// 产出 <c>&lt;图名&gt;.ocr.txt</c> / <c>.ocr.json</c>。
/// 退出码：0 成功；4 输入不可读；5 OCR 不可用或识别失败。
/// </summary>
internal static class OcrDump
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Run(string imagePath, string? roiText = null)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return 4;
        }

        ImageFrame full;
        try
        {
            full = BitmapCodec.LoadPng(imagePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
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

        using var engine = new WindowsMediaOcrEngine();

        string stem = Path.ChangeExtension(imagePath, null)
            + (roi.Width == full.Width && roi.Height == full.Height ? string.Empty : $".roi{roi.X}_{roi.Y}_{roi.Width}x{roi.Height}");

        // 诊断模式：把「原始 / 对比度拉伸 / 拉伸+2倍放大」三种预处理都跑一遍做对比。
        // 目的是回答「对比度不足是不是 OCR 失败的根因」。
        var variants = new (string Name, ImageFrame Frame)[]
        {
            ("原始", target),
            ("拉伸", ImagePreprocessor.ContrastStretch(target)),
            ("拉伸+2x", ImagePreprocessor.Upscale(ImagePreprocessor.ContrastStretch(target), 2)),
            ("二值化", ImagePreprocessor.Binarize(target)),
            ("二值化+2x", ImagePreprocessor.Upscale(ImagePreprocessor.Binarize(target), 2)),
        };

        var sb = new StringBuilder();
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"引擎: {engine.Name}    区域: {roi.X},{roi.Y},{roi.Width},{roi.Height}"));
        sb.AppendLine();

        int successCount = 0;

        foreach ((string name, ImageFrame prepared) in variants)
        {
            if (!engine.TryRecognize(prepared, out IReadOnlyList<OcrTextLine> variantLines, out string? variantError))
            {
                sb.AppendLine($"[{name}] 失败：{variantError}");
                continue;
            }

            successCount++;
            sb.AppendLine($"[{name}] {prepared.Width}x{prepared.Height} → {variantLines.Count} 行");
            foreach (OcrTextLine line in variantLines.OrderBy(l => l.Y))
            {
                sb.AppendLine($"    {line.Text}");
            }

            sb.AppendLine();
        }

        File.WriteAllText(stem + ".ocr.txt", sb.ToString());

        return successCount > 0 ? 0 : 5;
    }
}

