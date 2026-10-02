
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
        ReadOnlySpan<byte> src = crop.Bgra;
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
}
