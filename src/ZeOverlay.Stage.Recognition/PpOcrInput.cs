
using ZeOverlay.Shared;

namespace ZeOverlay.Stage.Recognition;

/// <summary>
/// PP-OCR rec 的输入预处理（纯逻辑，便于单测）：
/// 按长宽比缩放到 48 高、双线性采样、归一化到 [-1,1]，右侧补 0（中灰）。
/// 输出布局为 <c>[batch, 3, TargetHeight, targetWidth]</c>（通道优先，行间无 padding）。
/// </summary>
internal static class PpOcrInput
{
    public const int TargetHeight = 48;
    public const int MaxWidth = 1200;

    /// <summary>裁剪切到 48 高后的自然输入宽度（下限 8，上限 <see cref="MaxWidth"/>）。</summary>
    public static int NaturalWidth(int cropWidth, int cropHeight)
        => Math.Clamp((int)Math.Ceiling(TargetHeight * cropWidth / (double)cropHeight), 8, MaxWidth);

    /// <summary>把 <paramref name="crop"/> 缩放归一化后写入 <paramref name="dst"/> 第 <paramref name="batchIndex"/> 个样本。</summary>
    public static void Fill(ImageFrame crop, Span<float> dst, int batchIndex, int targetWidth)
    {
        int width = Math.Clamp((int)Math.Ceiling(TargetHeight * crop.Width / (double)crop.Height), 8, targetWidth);

        // 预计算每个输出列的源列与权重：原来这些除法/取整放在 y 循环内层，
        // 等于对每一行重复算一遍（48 次）。挪出来以后内层只做乘加。
        var x0s = new int[width];
        var x1s = new int[width];
        var fxs = new double[width];
        double scaleX = crop.Width / (double)width;

        for (int x = 0; x < width; x++)
        {
            double sx = (x + 0.5) * scaleX - 0.5;
            double floorX = Math.Floor(sx);
            int x0 = Math.Clamp((int)floorX, 0, crop.Width - 1);
            x0s[x] = x0;
            x1s[x] = Math.Min(x0 + 1, crop.Width - 1);
            fxs[x] = sx - floorX;
        }

        // 直接写底层 buffer：tensor[i,c,y,x] 的三维索引器每次都要算 stride + 边界检查，
        // 48×width×3 次下来是预处理里最贵的一块。
        // 低对比度（亮白底、白字贴白底）先做分位数拉伸，拉大字/底差异再送模型；
        // 对比度足够或全平的低对比帧不受影响。
        byte[]? stretched = BuildContrastStretch(crop);
        ReadOnlySpan<byte> src = stretched ?? crop.Bgra;
        int plane = TargetHeight * targetWidth;
        int dstBase = batchIndex * 3 * plane;
        double scaleY = crop.Height / (double)TargetHeight;

        for (int y = 0; y < TargetHeight; y++)
        {
            double sy = (y + 0.5) * scaleY - 0.5;
            double floorY = Math.Floor(sy);
            int y0 = Math.Clamp((int)floorY, 0, crop.Height - 1);
            int y1 = Math.Min(y0 + 1, crop.Height - 1);
            double fy = sy - floorY;
            double oneMinusFy = 1 - fy;
            int row0 = y0 * crop.Width;
            int row1 = y1 * crop.Width;

            for (int x = 0; x < width; x++)
            {
                int x0 = x0s[x];
                int x1 = x1s[x];
                double fx = fxs[x];
                double oneMinusFx = 1 - fx;

                int i00 = (row0 + x0) * 4;
                int i01 = (row0 + x1) * 4;
                int i10 = (row1 + x0) * 4;
                int i11 = (row1 + x1) * 4;

                // PP-OCR 用 cv2 读图 ⇒ BGR。ImageFrame 的通道 0..2 正好是 B,G,R。
                for (int c = 0; c < 3; c++)
                {
                    double top = src[i00 + c] * oneMinusFx + src[i01 + c] * fx;
                    double bottom = src[i10 + c] * oneMinusFx + src[i11 + c] * fx;
                    double value = top * oneMinusFy + bottom * fy;
                    dst[dstBase + c * plane + y * targetWidth + x] = (float)((value / 255.0 - 0.5) / 0.5);
                }
            }

            // 右侧补 0（归一化后 0 即中灰），与 PP-OCR 的 padding 一致
            for (int c = 0; c < 3; c++)
            {
                int rowBase = dstBase + c * plane + y * targetWidth;
                for (int x = width; x < targetWidth; x++)
                {
                    dst[rowBase + x] = 0;
                }
            }
        }
    }

    /// <summary>
    /// 低对比度裁剪的分位数拉伸（[p5,p95] → [0,255]）。只在动态范围 1..99 时启用：
    /// 范围足够（对比度好）或全平（无对比）时返回 null，保持原像素、避免放大噪声。
    /// 针对**亮白底/白字贴白底**导致 PP-OCR 掉字/读花的情况。internal 供单测。
    /// </summary>
    internal static byte[]? BuildContrastStretch(ImageFrame crop)
    {
        var histogram = new int[256];
        for (int y = 0; y < crop.Height; y++)
        {
            for (int x = 0; x < crop.Width; x++)
            {
                histogram[crop.LuminanceAt(x, y)]++;
            }
        }

        long total = (long)crop.Width * crop.Height;
        int low = Percentile(histogram, total, 5);
        int high = Percentile(histogram, total, 95);

        if (high - low < 1 || high - low >= 100)
        {
            return null;
        }

        var lut = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            lut[i] = (byte)Math.Clamp((i - low) * 255 / (high - low), 0, 255);
        }

        byte[] pixels = new byte[crop.Bgra.Length];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = lut[crop.Bgra[i]];
            pixels[i + 1] = lut[crop.Bgra[i + 1]];
            pixels[i + 2] = lut[crop.Bgra[i + 2]];
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    private static int Percentile(int[] histogram, long total, double percentile)
    {
        long target = (long)(total * percentile / 100.0);
        long running = 0;

        for (int i = 0; i < histogram.Length; i++)
        {
            running += histogram[i];
            if (running >= target)
            {
                return i;
            }
        }

        return histogram.Length - 1;
    }
}
