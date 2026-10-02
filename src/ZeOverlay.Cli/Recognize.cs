using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

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
/// 离线跑完整识别链路（OCR → 行解析 → 关注名单匹配），把结构化结果落盘。
/// 用途：在不开游戏的情况下用真实样本做回归验证，以及给关注名单挑名字。
///
/// 退出码：0 成功；4 输入不可读；5 OCR 不可用。
/// </summary>
internal static class RecognizeDump
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Run(string imagePath, string? roiText, IReadOnlyList<string> watchlist, double threshold, bool usePpOcr = false)
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

        var matcher = new Matcher(watchlist, new WatchlistOptions { Threshold = threshold });
        var parsed = new List<ParsedRow>();
        var rawTexts = new List<string>();
        string engineName;
        int sourceLineCount;

        if (usePpOcr)
        {
            Paths paths = Paths.ForExecutable();
            string modelPath = Path.Combine(paths.BaseDirectory, "models", "ch_PP-OCRv3_rec_infer.onnx");
            string keysPath = Path.Combine(paths.BaseDirectory, "models", "ppocr_keys_v1.txt");

            if (!File.Exists(modelPath) || !File.Exists(keysPath))
            {
                File.WriteAllText(imagePath + ".rec.error.txt", $"找不到 PP-OCR 模型：{modelPath}");
                return 5;
            }

            using var pp = new PpOcrEngine(modelPath, keysPath);
            engineName = pp.Name;

            RowReport report = Analyzer.Analyze(target);
            IReadOnlyList<RowCrop> crops = new GlyphSegmentationStage().Process(new RowInput(target, report));
            sourceLineCount = crops.Count;

            foreach (PpOcrRow row in pp.RecognizeCrops(crops))
            {
                rawTexts.Add($"{row.Text}   [置信 {row.Confidence:0.00}]");
                ParsedRow? rowParsed = Parser.Parse(row.Text);
                if (rowParsed is not null)
                {
                    parsed.Add(rowParsed);
                }
            }
        }
        else
        {
            using var ocr = new SystemOcrEngine();

            if (!ocr.TryRecognize(target, out IReadOnlyList<OcrTextLine> lines, out string? error))
            {
                return 5;
            }

            engineName = ocr.Name;
            sourceLineCount = lines.Count;

            foreach (OcrTextLine line in lines.OrderBy(l => l.Y))
            {
                ParsedRow? row = Parser.Parse(line.Text);
                if (row is not null)
                {
                    parsed.Add(row);
                }
            }
        }

        string stem = Path.ChangeExtension(imagePath, null)
            + (roi.Width == full.Width && roi.Height == full.Height ? string.Empty : $".roi{roi.X}_{roi.Y}_{roi.Width}x{roi.Height}")
            + ".rec";

        var sb = new StringBuilder();
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"引擎: {engineName}    输入行={sourceLineCount}    解析成功={parsed.Count}    名单={matcher.Entries.Count} 项"));
        sb.AppendLine();
        sb.AppendLine("  # | 名称           | 状态        | n/m  | 玩家名");
        sb.AppendLine("----+----------------+-------------+------+------------------------");

        foreach (ParsedRow row in parsed)
        {
            string status = row.State switch
            {
                ArtifactState.Ready => "[R]",
                ArtifactState.Cooling => $"[{row.CooldownSeconds}]",
                _ => "[?]",
            };

            string uses = row.UsesRemaining is { } r && row.UsesTotal is { } t ? $"{r}/{t}" : "-";
            string matched = matcher.Match(row.ArtifactName) is { } m ? $"  ←名单:{m.CanonicalName} ({m.Score:0.00})" : string.Empty;

            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{parsed.IndexOf(row),3} | {row.ArtifactName,-14} | {status,-11} | {uses,-4} | {row.PlayerName}{matched}"));
        }

        sb.AppendLine();
        sb.AppendLine("--- 原始识别文本 ---");
        foreach (string text in rawTexts)
        {
            sb.AppendLine("  " + text);
        }

        File.WriteAllText(stem + ".txt", sb.ToString());
        File.WriteAllText(stem + ".json", JsonSerializer.Serialize(new
        {
            engine = engineName,
            inputLineCount = sourceLineCount,
            parsedCount = parsed.Count,
            rawTexts,
            watchlist = matcher.Entries,
            rows = parsed.Select(r => new
            {
                artifact = r.ArtifactName,
                player = r.PlayerName,
                state = r.State.ToString(),
                cooldownSeconds = r.CooldownSeconds,
                usesRemaining = r.UsesRemaining,
                usesTotal = r.UsesTotal,
                matchedName = matcher.Match(r.ArtifactName)?.CanonicalName,
                raw = r.RawText,
            }),
        }, JsonOptions));

        return 0;
    }
}
