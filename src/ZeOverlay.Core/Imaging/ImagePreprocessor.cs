using ZeOverlay.Core.Analysis;

namespace ZeOverlay.Core.Imaging;

/// <summary>
/// 送 OCR 之前的图像预处理（`PLAN.md` 第 4 节采集层的「灰度/反色/放大/二值化」）。
///
/// 动机来自实测：同一块面板，背景是**较暗墙面**时系统 OCR 能读对 23/23 行；
/// 而背景是**很亮的浅灰墙**（白字与底的亮度差很小）时，OCR 只能返回 4~6 行乱码
/// （`黑色瓶中闪电 [R]1/1 黑色瓶中闪电` 被读成 `黑琶瓶·电《R]》1黑色疝`）。
/// 像素本身是清晰的（截图肉眼可读），所以问题在**对比度不足**，预处理能救。
/// </summary>
public static class ImagePreprocessor
{
    /// <summary>按分位数做对比度拉伸，让字与底的亮度差拉开。</summary>
    public static ImageFrame ContrastStretch(ImageFrame frame, double lowPercentile = 2.0, double highPercentile = 98.0)
    {
        ArgumentNullException.ThrowIfNull(frame);

        int[] histogram = new int[256];
        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                histogram[frame.LuminanceAt(x, y)]++;
            }
        }

        long total = (long)frame.Width * frame.Height;
        int low = Percentile(histogram, total, lowPercentile);
        int high = Percentile(histogram, total, highPercentile);

        if (high - low < 16)
        {
            // 本来就没什么动态范围，拉伸只会放大噪声
            return frame;
        }

        var lut = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int mapped = (i - low) * 255 / (high - low);
            lut[i] = (byte)Math.Clamp(mapped, 0, 255);
        }

        byte[] pixels = new byte[frame.Bgra.Length];
        Array.Copy(frame.Bgra, pixels, frame.Bgra.Length);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = lut[pixels[i]];
            pixels[i + 1] = lut[pixels[i + 1]];
            pixels[i + 2] = lut[pixels[i + 2]];
        }

        return new ImageFrame
        {
            Width = frame.Width,
            Height = frame.Height,
            Bgra = pixels,
            CapturedAtUnixMs = frame.CapturedAtUnixMs,
        };
    }

    /// <summary>放大（双线性）。小字号文本放大后系统 OCR 通常明显更稳。</summary>
    public static ImageFrame Upscale(ImageFrame frame, int factor)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (factor <= 1)
        {
            return frame;
        }

        int width = frame.Width * factor;
        int height = frame.Height * factor;
        byte[] pixels = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            double sy = (y + 0.5) / factor - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(sy), 0, frame.Height - 1);
            int y1 = Math.Min(y0 + 1, frame.Height - 1);
            double fy = sy - Math.Floor(sy);

            for (int x = 0; x < width; x++)
            {
                double sx = (x + 0.5) / factor - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(sx), 0, frame.Width - 1);
                int x1 = Math.Min(x0 + 1, frame.Width - 1);
                double fx = sx - Math.Floor(sx);

                int i00 = (y0 * frame.Width + x0) * 4;
                int i01 = (y0 * frame.Width + x1) * 4;
                int i10 = (y1 * frame.Width + x0) * 4;
                int i11 = (y1 * frame.Width + x1) * 4;
                int di = (y * width + x) * 4;

                for (int c = 0; c < 3; c++)
                {
                    double top = frame.Bgra[i00 + c] * (1 - fx) + frame.Bgra[i01 + c] * fx;
                    double bottom = frame.Bgra[i10 + c] * (1 - fx) + frame.Bgra[i11 + c] * fx;
                    pixels[di + c] = (byte)Math.Clamp(top * (1 - fy) + bottom * fy, 0, 255);
                }

                pixels[di + 3] = 255;
            }
        }

        return new ImageFrame
        {
            Width = width,
            Height = height,
            Bgra = pixels,
            CapturedAtUnixMs = frame.CapturedAtUnixMs,
        };
    }

    /// <summary>
    /// 二值化（Otsu）。输出**黑底白字**。
    /// 思路：本场景字是「白芯 + 深描边」叠在浅灰底上，亮芯与底的对比度很小，
    /// 而**描边与底的对比度更大**，二值化能把字形边界固定下来。
    /// </summary>
    public static ImageFrame Binarize(ImageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var luminance = new double[frame.Width * frame.Height];
        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                luminance[y * frame.Width + x] = frame.LuminanceAt(x, y);
            }
        }

        double threshold = OtsuThreshold.Compute(luminance);
        byte[] pixels = new byte[frame.Bgra.Length];

        for (int i = 0, p = 0; i < frame.Bgra.Length; i += 4, p++)
        {
            byte value = luminance[p] > threshold ? (byte)255 : (byte)0;
            pixels[i] = value;
            pixels[i + 1] = value;
            pixels[i + 2] = value;
            pixels[i + 3] = 255;
        }

        return new ImageFrame
        {
            Width = frame.Width,
            Height = frame.Height,
            Bgra = pixels,
            CapturedAtUnixMs = frame.CapturedAtUnixMs,
        };
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

