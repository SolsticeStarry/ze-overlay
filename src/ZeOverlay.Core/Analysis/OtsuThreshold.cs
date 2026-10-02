namespace ZeOverlay.Core.Analysis;

/// <summary>
/// Otsu 阈值。行剖分与字形切分都需要，抽出来共用，避免两处实现漂移。
/// </summary>
public static class OtsuThreshold
{
    private const int Bins = 256;

    public static double Compute(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
        {
            return 0;
        }

        double min = values[0];
        double max = values[0];

        foreach (double value in values)
        {
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        if (max - min < 1e-6)
        {
            return max;
        }

        int[] histogram = new int[Bins];
        double scale = (Bins - 1) / (max - min);

        foreach (double value in values)
        {
            int bin = (int)Math.Round((value - min) * scale);
            histogram[Math.Clamp(bin, 0, Bins - 1)]++;
        }

        long total = values.Count;
        double sumAll = 0;
        for (int i = 0; i < Bins; i++)
        {
            sumAll += (double)i * histogram[i];
        }

        long weightBackground = 0;
        double sumBackground = 0;
        double bestVariance = -1;
        int bestBin = 0;

        for (int i = 0; i < Bins; i++)
        {
            weightBackground += histogram[i];
            if (weightBackground == 0)
            {
                continue;
            }

            long weightForeground = total - weightBackground;
            if (weightForeground == 0)
            {
                break;
            }

            sumBackground += (double)i * histogram[i];

            double meanBackground = sumBackground / weightBackground;
            double meanForeground = (sumAll - sumBackground) / weightForeground;
            double between = (double)weightBackground * weightForeground
                * (meanBackground - meanForeground) * (meanBackground - meanForeground);

            if (between > bestVariance)
            {
                bestVariance = between;
                bestBin = i;
            }
        }

        return min + bestBin / scale;
    }
}
