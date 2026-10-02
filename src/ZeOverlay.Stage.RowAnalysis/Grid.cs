using ZeOverlay.Shared;

namespace ZeOverlay.Stage.RowAnalysis;

/// <summary>把行带拟合到等距网格上，剔除离群行带。</summary>
public static class Grid
{
    /// <summary>行距必须显著大于字高，也不会大到离谱。倍数由实测推出（样本 #1：字高 17、行距 30 ≈ 1.76 倍）。</summary>
    private const double MinPitchRatio = 1.2;
    private const double MaxPitchRatio = 4.0;
    private const double MinAbsolutePitch = 6.0;
    private const double SlotTolerance = 0.25;

    /// <summary>少于这个行数就无法可靠定出步长。</summary>
    public const int MinRows = 3;

    public static RowGrid? Estimate(IReadOnlyList<RowBand> bands, int frameHeight)
    {
        ArgumentNullException.ThrowIfNull(bands);

        if (bands.Count < MinRows)
        {
            return null;
        }

        double[] centers = bands.Select(b => (double)b.CenterY).OrderBy(c => c).ToArray();

        // 用**字高中位数**推出可行的行距范围，而不是写死像素值：
        // 行距必然大于字高（实测约 1.76 倍），也不会大到几倍字高以上。
        // 这样在换分辨率时自动跟随，同时能挡掉把窗口工具栏当成「行」的荒唐拟合。
        double[] heights = bands.Select(b => (double)b.Height).OrderBy(h => h).ToArray();
        double medianHeight = heights[heights.Length / 2];
        double minPitch = Math.Max(MinAbsolutePitch, medianHeight * MinPitchRatio);
        double maxPitch = Math.Max(minPitch * 1.5, Math.Min(frameHeight / 2.0, medianHeight * MaxPitchRatio));

        // 候选步长：相邻中心距，以及它的一半（可能漏检了一行）。
        var candidates = new List<double>();
        for (int i = 1; i < centers.Length; i++)
        {
            double delta = centers[i] - centers[i - 1];
            if (delta < minPitch || delta > maxPitch)
            {
                continue;
            }

            candidates.Add(delta);
            if (delta / 2 >= minPitch)
            {
                candidates.Add(delta / 2);
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        double bestPitch = 0;
        double bestPhase = 0;
        double bestScore = double.MinValue;
        double bestErrorSum = double.MaxValue;

        foreach (double pitch in candidates.Distinct())
        {
            foreach (double phase in centers)
            {
                int inliers = 0;
                double errorSum = 0;
                int minSlot = int.MaxValue;
                int maxSlot = int.MinValue;

                foreach (double center in centers)
                {
                    double slot = (center - phase) / pitch;
                    double error = Math.Abs(slot - Math.Round(slot));
                    if (error <= SlotTolerance)
                    {
                        int rounded = (int)Math.Round(slot);
                        inliers++;
                        errorSum += error * pitch;
                        minSlot = Math.Min(minSlot, rounded);
                        maxSlot = Math.Max(maxSlot, rounded);
                    }
                }

                if (inliers < MinRows || minSlot > maxSlot)
                {
                    continue;
                }

                // 关键：惩罚**空洞槽位**。
                // 否则更细的步长总能「吸收」更多噪声行带而胜出——
                // 实测踩过：真行距 31 被选成 15.5（正好一半），因为它把下方一个
                // 无关行带也算了进来。好网格应当用尽量少的槽位解释尽量多的行带。
                int span = maxSlot - minSlot + 1;
                int gapCount = span - inliers;
                double score = inliers * 10.0 - gapCount;

                bool better = score > bestScore + 1e-6
                    || (Math.Abs(score - bestScore) <= 1e-6 && errorSum < bestErrorSum - 1e-6)
                    || (Math.Abs(score - bestScore) <= 1e-6 && Math.Abs(errorSum - bestErrorSum) <= 1e-6 && pitch > bestPitch);

                if (better)
                {
                    bestScore = score;
                    bestErrorSum = errorSum;
                    bestPitch = pitch;
                    bestPhase = phase;
                }
            }
        }

        if (bestPitch <= 0)
        {
            return null;
        }

        // 用内点的残差中位数修正相位，降低单点抖动的影响。
        var residuals = new List<double>();
        foreach (double center in centers)
        {
            double slot = (center - bestPhase) / bestPitch;
            if (Math.Abs(slot - Math.Round(slot)) <= SlotTolerance)
            {
                residuals.Add(center - Math.Round(slot) * bestPitch);
            }
        }

        residuals.Sort();
        double refinedPhase = residuals[residuals.Count / 2];

        var inlierSlots = new List<int>();
        var outliers = new List<int>();

        for (int i = 0; i < bands.Count; i++)
        {
            double slot = (bands[i].CenterY - refinedPhase) / bestPitch;
            if (Math.Abs(slot - Math.Round(slot)) <= SlotTolerance)
            {
                inlierSlots.Add((int)Math.Round(slot));
            }
            else
            {
                outliers.Add(i);
            }
        }

        int firstSlot = inlierSlots.Min();
        int lastSlot = inlierSlots.Max();

        // 把相位归一化到「首行中心 = 槽位 0」。
        // 否则 FirstCenterY 只是某个相位基准（实测可能落在第 2 行上），
        // 后续按槽位取每行 y 坐标时会整体错位。
        double anchor = refinedPhase + firstSlot * bestPitch;

        var normalizedSlots = new List<int>(inlierSlots.Count);
        foreach (int slot in inlierSlots)
        {
            normalizedSlots.Add(slot - firstSlot);
        }

        var occupied = new HashSet<int>(normalizedSlots);
        var missing = new List<int>();
        for (int slot = 0; slot <= lastSlot - firstSlot; slot++)
        {
            if (!occupied.Contains(slot))
            {
                missing.Add(slot);
            }
        }

        return new RowGrid(
            anchor,
            bestPitch,
            lastSlot - firstSlot + 1,
            bands.Count,
            normalizedSlots.Count,
            outliers,
            missing);
    }
}
