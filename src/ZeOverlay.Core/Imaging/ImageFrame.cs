namespace ZeOverlay.Core.Imaging;

/// <summary>
/// 一帧 BGRA32（B,G,R,A 顺序）像素数据，行间无 padding，左上角为原点。
/// 这是采集层与识别层之间唯一的图像交换格式，与具体截屏后端解耦。
/// </summary>
public sealed class ImageFrame
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>长度必须为 Width * Height * 4。</summary>
    public required byte[] Bgra { get; init; }

    public required long CapturedAtUnixMs { get; init; }

    public int Stride => Width * 4;

    public int ByteCount => Width * Height * 4;

    public static ImageFrame FromBgra(int width, int height, byte[] bgra, long? capturedAtUnixMs = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(bgra);

        int expected = width * height * 4;
        if (bgra.Length < expected)
        {
            throw new ArgumentException($"像素缓冲过小：需要 {expected} 字节，实际 {bgra.Length} 字节。", nameof(bgra));
        }

        return new ImageFrame
        {
            Width = width,
            Height = height,
            Bgra = bgra,
            CapturedAtUnixMs = capturedAtUnixMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    /// <summary>亮度（0..255，BT.601 加权）。用于后续二值化前的灰度化。</summary>
    public byte LuminanceAt(int x, int y)
    {
        int i = (y * Width + x) * 4;
        byte b = Bgra[i];
        byte g = Bgra[i + 1];
        byte r = Bgra[i + 2];
        return (byte)((r * 299 + g * 587 + b * 114) / 1000);
    }

    /// <summary>平均亮度（抽样，用于快速判断是否黑帧与整体明暗）。</summary>
    public double MeanLuminance(int step = 3)
    {
        int s = Math.Max(1, step);
        long sum = 0;
        long count = 0;

        for (int y = 0; y < Height; y += s)
        {
            for (int x = 0; x < Width; x += s)
            {
                sum += LuminanceAt(x, y);
                count++;
            }
        }

        return count == 0 ? 0 : sum / (double)count;
    }

    /// <summary>裁出子区域（左上角相对本帧）。超出边界的部分按黑填充。</summary>
    public ImageFrame Crop(int x, int y, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        byte[] dst = new byte[width * height * 4];
        for (int row = 0; row < height; row++)
        {
            int srcY = y + row;
            if (srcY < 0 || srcY >= Height)
            {
                continue;
            }

            for (int col = 0; col < width; col++)
            {
                int srcX = x + col;
                if (srcX < 0 || srcX >= Width)
                {
                    continue;
                }

                int si = (srcY * Width + srcX) * 4;
                int di = (row * width + col) * 4;
                dst[di] = Bgra[si];
                dst[di + 1] = Bgra[si + 1];
                dst[di + 2] = Bgra[si + 2];
                dst[di + 3] = Bgra[si + 3];
            }
        }

        return new ImageFrame
        {
            Width = width,
            Height = height,
            Bgra = dst,
            CapturedAtUnixMs = CapturedAtUnixMs,
        };
    }
}
