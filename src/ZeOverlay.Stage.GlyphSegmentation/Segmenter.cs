
using ZeOverlay.Shared;

namespace ZeOverlay.Stage.GlyphSegmentation;

public sealed class SegmentOptions
{
    /// <summary>列间空白超过这么多像素才切断（0 = 只认「完全无墨迹」的间隙）。</summary>
    public int MinGap { get; set; }

    /// <summary>
    /// 谷值切分比例。**默认 0（关闭）**。
    ///
    /// 曾试图用它解决「汉字间隙只有 1~2 px」的问题（见 `docs/M2.md`），但实测**过度切分**：
    /// 汉字笔画之间本身也有墨迹凹陷，比例 0.5 会把一个字切成 2~4 片（每行 20~40 个碎片）。
    /// </summary>
    public double ValleyRatio { get; set; }

    /// <summary>判断「邻域峰值」时的左右窗口半径。</summary>
    public int ValleyWindow { get; set; } = 4;

    /// <summary>墨迹像素少于这么多的列区间丢弃（抗锯齿噪点）。</summary>
    public int MinInkPixels { get; set; } = 3;

    /// <summary>行带上下各多取几行，避免描边被切掉。</summary>
    public int VerticalPadding { get; set; } = 2;

    /// <summary>
    /// 墨迹掩码的腐蚀次数。**默认 0（关闭）**。
    ///
    /// 曾用它断开抗锯齿造成的字间桥接，但实测**有害**：本场景汉字在 17~19 px 字号下笔画很细，
    /// 腐蚀一次就把笔画吃掉，字形碎成 `1x7`、`2x3` 这类碎片（见 `docs/M2.md`）。
    /// </summary>
    public int ErodeIterations { get; set; }
}

/// <summary>
/// 行内字形切分（M2 第一步）。
///
/// 招式：行带内做 **Otsu 亮度阈值** → 墨迹掩码 → **列投影**切出字形。
/// 为什么这里能用亮度阈值，而行剖分不能：行剖分要在任意场景上找「有没有文字」，
/// 而这里已经**锁定在列表行内**，且该 UI 是「亮字 + 深色描边」，字芯亮度显著高于列表底色
/// （实测样本 #1/#2 的列表底色亮度约 100，字芯接近 240）。
///
/// 注意：只取**亮字芯**、不含深色描边——描边是连通的，带上它会把相邻字形粘成一个。
/// </summary>
public static class Segmenter
{
    public static GlyphLine SegmentRow(ImageFrame frame, int top, int bottom, SegmentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        options ??= new SegmentOptions();

        int y0 = Math.Max(0, top - options.VerticalPadding);
        int y1 = Math.Min(frame.Height - 1, bottom + options.VerticalPadding);

        if (y1 < y0)
        {
            return new GlyphLine([], 0, 0, y0, y0);
        }

        int height = y1 - y0 + 1;
        int width = frame.Width;

        var luminance = new double[height * width];
        double sum = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte value = frame.LuminanceAt(x, y0 + y);
                luminance[y * width + x] = value;
                sum += value;
            }
        }

        double mean = sum / luminance.Length;
        double threshold = ComputeIterativeOtsu(luminance);

        // 兜底：三角法也失效时（分布平坦/无亮尾）阈值可能落到背景里，
        // 至少要显著高于中位数，否则会把背景纹理当成墨迹。
        var sorted = (double[])luminance.Clone();
        Array.Sort(sorted);
        double median = sorted[sorted.Length / 2];
        double floor = median + Math.Max(12, (sorted[^1] - median) * 0.25);

        if (threshold < floor)
        {
            threshold = floor;
        }

        // 列投影（先腐蚀，断开抗锯齿造成的字间桥接）
        bool[] ink = new bool[height * width];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                ink[y * width + x] = luminance[y * width + x] > threshold;
            }
        }

        for (int i = 0; i < options.ErodeIterations; i++)
        {
            ink = Erode(ink, width, height);
        }

        var columnInk = new int[width];
        var columnTop = new int[width];
        var columnBottom = new int[width];

        for (int x = 0; x < width; x++)
        {
            columnTop[x] = int.MaxValue;
            columnBottom[x] = -1;

            for (int y = 0; y < height; y++)
            {
                if (ink[y * width + x])
                {
                    columnInk[x]++;

                    if (y < columnTop[x])
                    {
                        columnTop[x] = y;
                    }

                    if (y > columnBottom[x])
                    {
                        columnBottom[x] = y;
                    }
                }
            }
        }

        var glyphs = new List<Glyph>();
        int runStart = -1;
        int lastInk = -1;

        for (int x = 0; x < width; x++)
        {
            if (!IsValley(columnInk, x, options))
            {
                if (runStart < 0)
                {
                    runStart = x;
                }

                lastInk = x;
            }
            else if (runStart >= 0 && x - lastInk > options.MinGap)
            {
                AddGlyph(glyphs, columnInk, columnTop, columnBottom, runStart, lastInk, y0, options.MinInkPixels);
                runStart = -1;
            }
        }

        if (runStart >= 0)
        {
            AddGlyph(glyphs, columnInk, columnTop, columnBottom, runStart, lastInk, y0, options.MinInkPixels);
        }

        return new GlyphLine(glyphs, threshold, mean, y0, y1);
    }

    /// <summary>某列是否算「无墨迹」。ValleyRatio &gt; 0 时改用邻域凹陷判据。</summary>
    private static bool IsValley(int[] columnInk, int x, SegmentOptions options)
    {
        if (options.ValleyRatio <= 0)
        {
            return columnInk[x] == 0;
        }

        int peak = 0;
        for (int i = Math.Max(0, x - options.ValleyWindow); i <= Math.Min(columnInk.Length - 1, x + options.ValleyWindow); i++)
        {
            peak = Math.Max(peak, columnInk[i]);
        }

        return peak <= 0 || columnInk[x] < Math.Max(1, options.ValleyRatio * peak);
    }

    /// <summary>
    /// 两段式 Otsu：先在全体上分一次，再在「亮子集」上分**一次**。
    ///
    /// 为什么单次阈值法都不行（实测第 1 行带 y=40..62，384 宽）：
    /// 背景亮度横跨 **60~190**（左侧墙面 162~187），字芯 **216~255**。
    /// 单次 Otsu 落到 133、三角法落到 141，都在背景内部 ⇒ 墙面被当成墨迹、汉字粘成一块。
    /// 在亮子集（背景亮部 + 字芯）上再分一次，才落在 216 与 190 之间那条真正有用的界上。
    ///
    /// 为什么**只细化一次**：迭代到收敛会跑到 242，把字芯自己劈开、字形碎成碎片。
    /// </summary>
    private static double ComputeIterativeOtsu(double[] luminance)
    {
        double threshold = Otsu.Compute(luminance);

        var bright = new List<double>(luminance.Length / 4);
        foreach (double value in luminance)
        {
            if (value > threshold)
            {
                bright.Add(value);
            }
        }

        if (bright.Count >= 32)
        {
            double refined = Otsu.Compute(bright);
            if (refined > threshold)
            {
                threshold = refined;
            }
        }

        return threshold;
    }

    /// <summary>四邻域腐蚀一次。</summary>
    private static bool[] Erode(bool[] source, int width, int height)
    {
        var result = new bool[source.Length];

        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int i = y * width + x;
                if (!source[i])
                {
                    continue;
                }

                result[i] = source[i - 1] && source[i + 1]
                    && source[i - width] && source[i + width];
            }
        }

        return result;
    }

    private static void AddGlyph(
        List<Glyph> glyphs,
        int[] columnInk,
        int[] columnTop,
        int[] columnBottom,
        int left,
        int right,
        int y0,
        int minInkPixels)
    {
        int ink = 0;
        int top = int.MaxValue;
        int bottom = -1;

        for (int x = left; x <= right; x++)
        {
            ink += columnInk[x];

            if (columnInk[x] == 0)
            {
                continue;
            }

            top = Math.Min(top, columnTop[x]);
            bottom = Math.Max(bottom, columnBottom[x]);
        }

        if (ink < minInkPixels || bottom < top)
        {
            return;
        }

        glyphs.Add(new Glyph(left, right, y0 + top, y0 + bottom, ink));
    }
}
