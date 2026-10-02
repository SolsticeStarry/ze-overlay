using ZeOverlay.Core.Imaging;

namespace ZeOverlay.Core.Analysis;

/// <summary>一条文本行的像素带（由行方向梯度能量切分得到）。</summary>
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

/// <summary>行剖分结果。列位置只作参考，真正的解析靠括号锚定（见 PLAN 第 6.2 节）。</summary>
public sealed class RowProfileReport
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required double MeanLuminance { get; init; }

    /// <summary>行梯度能量的 Otsu 阈值。</summary>
    public required double Threshold { get; init; }

    public required IReadOnlyList<RowBand> Bands { get; init; }

    /// <summary>
    /// 横向墨迹区域。正常情况只应有 1 个（列表本身）。
    /// 出现多个说明 ROI 框宽了——把聊天栏 / 其它 HUD 一起框了进来，
    /// 那部分每帧都变，会直接废掉 PLAN 第 10 节的「ROI 无变化则复用上帧」优化。
    /// </summary>
    public required IReadOnlyList<InkRegion> Regions { get; init; }

    /// <summary>行网格拟合结果；行数不足以定出步长时为 null。</summary>
    public required RowGridEstimate? Grid { get; init; }

    /// <summary>吸附到网格内的行带（无网格时等于 <see cref="Bands"/>）。后续解析只用这一组。</summary>
    public required IReadOnlyList<RowBand> GridBands { get; init; }

    /// <summary>落在网格之外的离群行带：场景亮物件、窗口边缘等噪声。</summary>
    public required IReadOnlyList<RowBand> OutlierBands { get; init; }

    /// <summary>稳健行数：优先取网格内点数，避免被离群行带带偏。</summary>
    public int RowCount => Grid?.InlierCount ?? Bands.Count;

    public int RegionCount => Regions.Count;

    /// <summary>ROI 疑似框宽时的中文告警；正常（单区域）返回 null。</summary>
    public string? RegionWarning => RegionCount <= 1
        ? null
        : $"ROI 内检出 {RegionCount} 个横向区域（{string.Join(" / ", Regions.Select(r => $"{r.Left}..{r.Right}"))}）。"
          + "通常说明把聊天栏或其它 HUD 一起框了进来——那部分每帧都变，会废掉「ROI 无变化则复用上帧」的优化。建议只框住神器列表。";

    /// <summary>相邻行带中心距的中位数；行数 &lt; 2 时为 null。</summary>
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

/// <summary>
/// 行剖分：用「行方向水平梯度能量」找文本行。
///
/// 为什么不用亮度阈值：真实画面里列表叠在游戏场景上（墙、告示牌、聊天框），
/// 背景亮度不固定，且文字是「亮字 + 深色描边」。梯度能量对底色不敏感，
/// 亮字与描边都会产生强梯度，因此更稳。
/// </summary>
public static class RowProfileAnalyzer
{
    public static RowProfileReport Analyze(ImageFrame frame, int minGap = 2, int minBandHeight = 5)
    {
        ArgumentNullException.ThrowIfNull(frame);

        double[] rowScores = ComputeRowScores(frame);
        double minScore = rowScores.Min();
        double maxScore = rowScores.Max();

        // 退化判定必须看「分布本身是否平坦」，不能用「算出来的阈值是否为 0」——
        // Otsu 在双簇完全分离时**合法地**返回下界（可能是 0），据此走兜底会把阈值抬到
        // 远高于最大能量，导致一行都检不出来。
        double threshold = maxScore;
        IReadOnlyList<RowBand> bands = [];

        if (maxScore - minScore > 1e-6)
        {
            threshold = OtsuThreshold.Compute(rowScores);

            if (threshold >= maxScore)
            {
                // Otsu 极端分布下退化到上界时兜底取中值，保证仍能分出文本行。
                threshold = (minScore + maxScore) / 2.0;
            }

            bands = SegmentBands(frame, rowScores, threshold, minGap, minBandHeight);
        }

        RowGridEstimate? grid = RowGridEstimator.Estimate(bands, frame.Height);

        IReadOnlyList<RowBand> gridBands = bands;
        IReadOnlyList<RowBand> outlierBands = [];

        if (grid is not null)
        {
            var keep = new List<RowBand>();
            var drop = new List<RowBand>();
            var outliers = new HashSet<int>(grid.OutlierBandIndices);

            for (int i = 0; i < bands.Count; i++)
            {
                (outliers.Contains(i) ? drop : keep).Add(bands[i]);
            }

            gridBands = keep;
            outlierBands = drop;
        }

        return new RowProfileReport
        {
            Width = frame.Width,
            Height = frame.Height,
            MeanLuminance = frame.MeanLuminance(),
            Threshold = threshold,
            Bands = bands,
            Regions = ComputeInkRegions(frame),
            Grid = grid,
            GridBands = gridBands,
            OutlierBands = outlierBands,
        };
    }

    /// <summary>
    /// 找出横向连续的墨迹区间（跨所有行累加列梯度）。
    ///
    /// 两级合并：
    /// 1. 文字/单词之间的空隙很小 → 用 <paramref name="gapTolerance"/> 合并成一段。
    /// 2. 再判断这些段之间是否「离得足够远」：只有间隔超过 <c>max(48, 宽度/4)</c> 才视为**不同区域**。
    ///    这一步是必须的——场景里的亮物件（例如白桶）也会产生强梯度，
    ///    若只按小间隙切分，就会把场景物件误报成「多框了聊天栏」。
    /// </summary>
    private static List<InkRegion> ComputeInkRegions(ImageFrame frame, int gapTolerance = 16, int minRegionWidth = 12)
    {
        double[] columnScores = new double[frame.Width];
        double peak = 0;

        for (int x = 0; x < frame.Width; x++)
        {
            long sum = 0;
            for (int y = 0; y < frame.Height; y++)
            {
                int current = frame.LuminanceAt(x, y);
                int next = x + 1 < frame.Width ? frame.LuminanceAt(x + 1, y) : current;
                int previous = x > 0 ? frame.LuminanceAt(x - 1, y) : current;
                sum += Math.Max(Math.Abs(next - current), Math.Abs(current - previous));
            }

            columnScores[x] = sum;
            peak = Math.Max(peak, sum);
        }

        var runs = new List<InkRegion>();

        // 用峰值的固定比例当门限：背景渐变也会产生极小的恒定梯度，不能直接和 0 比较。
        double cut = peak * 0.05;
        if (cut <= 0)
        {
            return runs;
        }

        int runStart = -1;
        int lastInk = -1;

        for (int x = 0; x < frame.Width; x++)
        {
            if (columnScores[x] >= cut)
            {
                if (runStart < 0)
                {
                    runStart = x;
                }

                lastInk = x;
            }
            else if (runStart >= 0 && x - lastInk > gapTolerance)
            {
                AddRegion(runs, runStart, lastInk, minRegionWidth);
                runStart = -1;
            }
        }

        if (runStart >= 0)
        {
            AddRegion(runs, runStart, lastInk, minRegionWidth);
        }

        // 二级合并：把「离得不够远」的段并回同一区域。
        int clusterGap = Math.Max(48, frame.Width / 4);
        var regions = new List<InkRegion>();

        foreach (InkRegion run in runs)
        {
            if (regions.Count > 0 && run.Left - regions[^1].Right <= clusterGap)
            {
                regions[^1] = regions[^1] with { Right = Math.Max(regions[^1].Right, run.Right) };
            }
            else
            {
                regions.Add(run);
            }
        }

        return regions;
    }

    private static void AddRegion(List<InkRegion> regions, int left, int right, int minRegionWidth)
    {
        if (right - left + 1 >= minRegionWidth)
        {
            regions.Add(new InkRegion(left, right));
        }
    }

    private static List<RowBand> SegmentBands(
        ImageFrame frame,
        double[] rowScores,
        double threshold,
        int minGap,
        int minBandHeight)
    {
        var bands = new List<RowBand>();
        int y = 0;

        while (y < frame.Height)
        {
            // 严格大于：Otsu 可能把阈值定在低簇边界上。
            if (rowScores[y] <= threshold)
            {
                y++;
                continue;
            }

            int start = y;
            int lastOn = y;

            while (y < frame.Height)
            {
                if (rowScores[y] > threshold)
                {
                    lastOn = y;
                }
                else if (y - lastOn > minGap)
                {
                    break;
                }

                y++;
            }

            int end = lastOn;
            if (end - start + 1 >= minBandHeight)
            {
                (int left, int right) = ComputeExtent(frame, start, end);
                double meanScore = 0;
                double peak = 0;

                for (int row = start; row <= end; row++)
                {
                    meanScore += rowScores[row];
                    peak = Math.Max(peak, rowScores[row]);
                }

                bands.Add(new RowBand(bands.Count, start, end, left, right, meanScore / (end - start + 1), peak));
            }
        }

        return bands;
    }

    /// <summary>每行的水平梯度能量之和：sum |L(x+1,y) - L(x,y)|。</summary>
    private static double[] ComputeRowScores(ImageFrame frame)
    {
        double[] scores = new double[frame.Height];

        for (int y = 0; y < frame.Height; y++)
        {
            long sum = 0;
            int previous = frame.LuminanceAt(0, y);

            for (int x = 1; x < frame.Width; x++)
            {
                int current = frame.LuminanceAt(x, y);
                sum += Math.Abs(current - previous);
                previous = current;
            }

            scores[y] = sum;
        }

        return scores;
    }

    /// <summary>行带内的横向范围：列梯度能量达到峰值一定比例的 x 区间。</summary>
    private static (int Left, int Right) ComputeExtent(ImageFrame frame, int top, int bottom)
    {
        double[] columnScores = new double[frame.Width];
        double peak = 0;

        for (int x = 0; x < frame.Width; x++)
        {
            long sum = 0;
            for (int y = top; y <= bottom; y++)
            {
                int current = frame.LuminanceAt(x, y);
                int next = x + 1 < frame.Width ? frame.LuminanceAt(x + 1, y) : current;
                int previous = x > 0 ? frame.LuminanceAt(x - 1, y) : current;

                // 同时看左右两个方向：否则最右一列永远算不出梯度，横向范围会少 1 px。
                sum += Math.Max(Math.Abs(next - current), Math.Abs(current - previous));
            }

            columnScores[x] = sum;
            peak = Math.Max(peak, sum);
        }

        if (peak <= 0)
        {
            return (0, frame.Width - 1);
        }

        double cut = peak * 0.15;
        int left = 0;
        int right = frame.Width - 1;

        while (left < right && columnScores[left] < cut)
        {
            left++;
        }

        while (right > left && columnScores[right] < cut)
        {
            right--;
        }

        return (left, right);
    }
}
