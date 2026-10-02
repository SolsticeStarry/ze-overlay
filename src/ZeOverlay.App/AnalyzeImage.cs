using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ZeOverlay.Core.Analysis;
using ZeOverlay.Core.Geometry;
using ZeOverlay.Core.Imaging;
using ZeOverlay.Platform.Windows;

namespace ZeOverlay.App;

/// <summary>
/// 离线量测：对一张真实截图（可指定 ROI）做行剖分，输出 JSON / 文本 / 标注图三份结果。
///
/// 用法：ZeOverlay.exe --analyze-image &lt;png&gt; [--roi x,y,w,h]
///
/// 指定 ROI 时**先裁剪再分析**，否则整屏里的其它 HUD（顶部头像栏、左侧装备面板、
/// 左下聊天栏、右下武器栏）会一起被当成「文本行」，量出的行高/行距没有意义。
/// 输出的坐标一律换算回**原图坐标系**，便于直接对照截图。
///
/// 退出码：0 = 成功；4 = 输入不存在 / 无法读取 / ROI 非法。
/// </summary>
internal static class AnalyzeImage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Run(string imagePath, string? roiText = null, bool withGlyphs = false)
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

        ImageFrame analyzed = roi.X == 0 && roi.Y == 0 && roi.Width == full.Width && roi.Height == full.Height
            ? full
            : full.Crop(roi.X, roi.Y, roi.Width, roi.Height);

        RowProfileReport local = RowProfileAnalyzer.Analyze(analyzed);

        // 换算回原图坐标，方便直接对着截图读。
        RowBand Shift(RowBand band) => band with
        {
            Top = band.Top + roi.Y,
            Bottom = band.Bottom + roi.Y,
            Left = band.Left + roi.X,
            Right = band.Right + roi.X,
        };

        var report = new RowProfileReport
        {
            Width = full.Width,
            Height = full.Height,
            MeanLuminance = local.MeanLuminance,
            Threshold = local.Threshold,
            Bands = local.Bands.Select(Shift).ToList(),
            Regions = local.Regions
                .Select(r => r with { Left = r.Left + roi.X, Right = r.Right + roi.X })
                .ToList(),
            Grid = local.Grid is null
                ? null
                : local.Grid with { FirstCenterY = local.Grid.FirstCenterY + roi.Y },
            GridBands = local.GridBands.Select(Shift).ToList(),
            OutlierBands = local.OutlierBands.Select(Shift).ToList(),
        };

        string stem = Path.ChangeExtension(imagePath, null) + (roi.Width == full.Width && roi.Height == full.Height ? string.Empty : $".roi{roi.X}_{roi.Y}_{roi.Width}x{roi.Height}");

        File.WriteAllText(stem + ".rows.json", JsonSerializer.Serialize(ToJson(full, roi, report), JsonOptions));
        File.WriteAllText(stem + ".rows.txt", BuildText(full, roi, report));

        // 标注图画在**原图**上（带 ROI 上下文），便于目视校验。
        BitmapCodec.SavePng(BandOverlayRenderer.DrawBands(full, report.Bands), stem + ".rows.png");

        if (withGlyphs)
        {
            RunGlyphs(stem, analyzed, local, roi);
        }

        return 0;
    }

    /// <summary>
    /// M2 第一步：把每个列表行切成字形，输出可视化图 + 清单。
    /// 在 **ROI 裁剪图**上做切分与标注（原图整宽会把同一行上其它位置的内容也切进来）。
    /// </summary>
    private static void RunGlyphs(string stem, ImageFrame analyzed, RowProfileReport local, PixelRect roi)
    {
        var allGlyphs = new List<Glyph>();
        var lines = new List<object>();
        var text = new StringBuilder();

        text.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"ROI={roi.X},{roi.Y},{roi.Width},{roi.Height}    行数={local.GridBands.Count}（以下坐标相对 ROI）"));
        text.AppendLine("行 | 字形数 | 各字形 左..右 (宽×高 墨迹)");

        for (int i = 0; i < local.GridBands.Count; i++)
        {
            RowBand band = local.GridBands[i];
            GlyphLine line = GlyphSegmenter.SegmentRow(analyzed, band.Top, band.Bottom);

            allGlyphs.AddRange(line.Glyphs);

            lines.Add(new
            {
                row = i,
                bandTop = band.Top,
                bandBottom = band.Bottom,
                inkThreshold = Math.Round(line.InkThreshold, 2),
                background = Math.Round(line.BackgroundLuminance, 2),
                glyphCount = line.Glyphs.Count,
                glyphs = line.Glyphs.Select(g => new
                {
                    left = g.Left,
                    right = g.Right,
                    top = g.Top,
                    bottom = g.Bottom,
                    width = g.Width,
                    height = g.Height,
                    ink = g.InkPixels,
                }),
            });

            string glyphText = string.Join(
                " ",
                line.Glyphs.Select(g => $"{g.Left}..{g.Right}({g.Width}x{g.Height},{g.InkPixels})"));

            text.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{i,2} | {line.Glyphs.Count,6} | {glyphText}"));
        }

        File.WriteAllText(stem + ".glyphs.json", JsonSerializer.Serialize(
            new { glyphLines = lines, totalGlyphs = allGlyphs.Count },
            JsonOptions));
        File.WriteAllText(stem + ".glyphs.txt", text.ToString());

        BitmapCodec.SavePng(GlyphOverlayRenderer.Draw(analyzed, allGlyphs), stem + ".glyphs.png");
    }

    private static object ToJson(ImageFrame frame, PixelRect roi, RowProfileReport report) => new
    {
        image = new { width = frame.Width, height = frame.Height },
        analyzedRegion = new { x = roi.X, y = roi.Y, width = roi.Width, height = roi.Height },
        meanLuminance = Math.Round(report.MeanLuminance, 2),
        threshold = Math.Round(report.Threshold, 2),
        bandCount = report.Bands.Count,
        medianPitch = report.MedianPitch,
        robustRowCount = report.RowCount,
        detectedBands = report.Bands.Count,
        outlierBands = report.OutlierBands.Count,
        grid = report.Grid is null
            ? null
            : new
            {
                firstCenterY = Math.Round(report.Grid.FirstCenterY, 2),
                pitch = Math.Round(report.Grid.Pitch, 2),
                slotCount = report.Grid.SlotCount,
                inlierCount = report.Grid.InlierCount,
                inlierRatio = Math.Round(report.Grid.InlierRatio, 3),
                missingSlots = report.Grid.MissingSlots,
            },
        regionCount = report.RegionCount,
        regionWarning = report.RegionWarning,
        regions = report.Regions.Select(r => new { left = r.Left, right = r.Right, width = r.Width }),
        bands = report.Bands.Select(b => new
        {
            index = b.Index,
            top = b.Top,
            bottom = b.Bottom,
            height = b.Height,
            left = b.Left,
            right = b.Right,
            width = b.Width,
            centerY = b.CenterY,
            meanScore = Math.Round(b.MeanScore, 2),
            peakScore = Math.Round(b.PeakScore, 2),
        }),
    };

    private static string BuildText(ImageFrame frame, PixelRect roi, RowProfileReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"图像: {frame.Width} x {frame.Height}    分析区域: {roi.X},{roi.Y},{roi.Width},{roi.Height}    区域平均亮度: {report.MeanLuminance:0.00}"));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"行梯度能量阈值(Otsu): {report.Threshold:0.00}    检出文本行: {report.Bands.Count}    行距中位数: {report.MedianPitch?.ToString("0.##", CultureInfo.InvariantCulture) ?? "n/a"}"));

        if (report.Grid is { } grid)
        {
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"行网格: 首行中心Y={grid.FirstCenterY:0.##}  行距={grid.Pitch:0.##}  槽位={grid.SlotCount}  内点={grid.InlierCount}  离群={report.OutlierBands.Count}  内点率={grid.InlierRatio:P0}  缺槽={string.Join(",", grid.MissingSlots)}"));
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"稳健行数: {report.RowCount}（检出 {report.Bands.Count} − 离群 {report.OutlierBands.Count}）"));
        }
        else
        {
            sb.AppendLine($"行网格: 行数不足（<{RowGridEstimator.MinRows}），无法定步长；稳健行数 = 检出 {report.Bands.Count}");
        }
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"横向墨迹区域: {report.RegionCount}    {string.Join(" / ", report.Regions.Select(r => $"{r.Left}..{r.Right}({r.Width}px)"))}"));

        if (report.RegionWarning is not null)
        {
            sb.AppendLine();
            sb.AppendLine("⚠ " + report.RegionWarning);
        }

        sb.AppendLine();
        sb.AppendLine("（以下坐标均为原图像素坐标）");
        sb.AppendLine(" idx |   top..bottom  | 高 |   left..right   | 宽 | 中心Y | 平均能量 | 峰值能量");
        sb.AppendLine("-----+----------------+----+-----------------+----+-------+----------+---------");

        foreach (RowBand band in report.Bands)
        {
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{band.Index,4} | {band.Top,5}..{band.Bottom,-7} | {band.Height,2} | {band.Left,6}..{band.Right,-7} | {band.Width,3} | {band.CenterY,5} | {band.MeanScore,8:0} | {band.PeakScore,8:0}"));
        }

        return sb.ToString();
    }
}
