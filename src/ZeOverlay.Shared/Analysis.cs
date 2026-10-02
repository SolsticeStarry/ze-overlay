namespace ZeOverlay.Shared;

/// <summary>一条文本行的像素带。</summary>
public sealed record RowBand(
    int Index,
    int Top,
    int Bottom,
    int Left,
    int Right,
    double MeanScore,
    double PeakScore)
{
    public int Height => Bottom - Top + 1;

    public int Width => Right - Left + 1;

    public int CenterY => (Top + Bottom) / 2;
}

/// <summary>横向上一段连续有「墨迹」的列区间。</summary>
public sealed record InkRegion(int Left, int Right)
{
    public int Width => Right - Left + 1;
}

/// <summary>拟合出来的等距行网格。</summary>
public sealed record RowGrid(
    double FirstCenterY,
    double Pitch,
    int SlotCount,
    int BandCount,
    int InlierCount,
    IReadOnlyList<int> OutlierBandIndices,
    IReadOnlyList<int> MissingSlots)
{
    public double Tolerance => Pitch * 0.25;

    public double InlierRatio => BandCount <= 0 ? 0 : InlierCount / (double)BandCount;

    public double CenterOf(int slot) => FirstCenterY + slot * Pitch;

    public int? SlotOf(double centerY)
    {
        if (Pitch <= 0)
        {
            return null;
        }

        double slot = (centerY - FirstCenterY) / Pitch;
        int rounded = (int)Math.Round(slot);
        return Math.Abs(slot - rounded) * Pitch <= Tolerance ? rounded : null;
    }
}

/// <summary>行剖分结果。</summary>
public sealed class RowReport
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required double MeanLuminance { get; init; }

    /// <summary>行梯度能量的 Otsu 阈值。</summary>
    public required double Threshold { get; init; }

    public required IReadOnlyList<RowBand> Bands { get; init; }

    /// <summary>横向墨迹区域；正常只应有 1 个（列表本身）。</summary>
    public required IReadOnlyList<InkRegion> Regions { get; init; }

    /// <summary>行网格；行数不足以定步长时为 null。</summary>
    public required RowGrid? Grid { get; init; }

    /// <summary>吸附到网格内的行带；无网格时等于 <see cref="Bands"/>。</summary>
    public required IReadOnlyList<RowBand> GridBands { get; init; }

    /// <summary>落在网格之外的离群行带。</summary>
    public required IReadOnlyList<RowBand> OutlierBands { get; init; }

    public int RowCount => Grid?.InlierCount ?? Bands.Count;

    public int RegionCount => Regions.Count;

    public string? RegionWarning => RegionCount <= 1
        ? null
        : $"ROI 内检出 {RegionCount} 个横向区域（{string.Join(" / ", Regions.Select(r => $"{r.Left}..{r.Right}"))}）。"
          + "通常说明把聊天栏或其它 HUD 一起框了进来——那部分每帧都变，会废掉「ROI 无变化则复用上帧」的优化。建议只框住神器列表。";

    public double? MedianPitch
    {
        get
        {
            if (Bands.Count < 2)
            {
                return null;
            }

            var pitches = new List<double>();
            for (int i = 1; i < Bands.Count; i++)
            {
                pitches.Add(Bands[i].CenterY - Bands[i - 1].CenterY);
            }

            pitches.Sort();
            int mid = pitches.Count / 2;
            return pitches.Count % 2 == 1
                ? pitches[mid]
                : (pitches[mid - 1] + pitches[mid]) / 2.0;
        }
    }
}

/// <summary>一行裁剪（槽位 + 行带 + 裁剪图像），S2 产出、S3 消费。</summary>
public sealed record RowCrop(int Slot, RowBand Band, ImageFrame Image);

/// <summary>S2 的输入：帧 + 行分析结果。</summary>
public sealed record RowInput(ImageFrame Frame, RowReport Report);

/// <summary>一个字形（连通列区间 + 垂直范围）。</summary>
public sealed record Glyph(int Left, int Right, int Top, int Bottom, int InkPixels)
{
    public int Width => Right - Left + 1;

    public int Height => Bottom - Top + 1;

    public int CenterX => (Left + Right) / 2;
}

/// <summary>一行文本的字形切分结果。</summary>
public sealed record GlyphLine(
    IReadOnlyList<Glyph> Glyphs,
    double InkThreshold,
    double BackgroundLuminance,
    int Top,
    int Bottom);

