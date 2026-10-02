using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ZeOverlay.Core.Imaging;

namespace ZeOverlay.Platform.Windows;

/// <summary>ImageFrame 与 PNG 之间的编解码（用于截图留档与回归集）。</summary>
public static class BitmapCodec
{
    public static void SavePng(ImageFrame frame, string path)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var bitmap = ToBitmap(frame);
        bitmap.Save(path, ImageFormat.Png);
    }

    public static ImageFrame LoadPng(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var bitmap = new Bitmap(path);
        byte[] buffer = ReadBgra(bitmap, bitmap.Width, bitmap.Height);
        return ImageFrame.FromBgra(bitmap.Width, bitmap.Height, buffer);
    }

    /// <summary>从 Bitmap 取出 BGRA 像素并补齐 alpha（GDI+ 读出的 alpha 不可靠）。</summary>
    private static byte[] ReadBgra(Bitmap bitmap, int width, int height)
    {
        var data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            byte[] buffer = new byte[width * height * 4];
            int rowBytes = width * 4;

            if (data.Stride == rowBytes)
            {
                Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), buffer, y * rowBytes, rowBytes);
                }
            }

            for (int i = 3; i < buffer.Length; i += 4)
            {
                buffer[i] = 255;
            }

            return buffer;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>把一个 Bitmap 转成 ImageFrame（会做一次像素拷贝）。</summary>
    public static ImageFrame FromBitmap(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        byte[] buffer = ReadBgra(bitmap, bitmap.Width, bitmap.Height);
        return ImageFrame.FromBgra(bitmap.Width, bitmap.Height, buffer);
    }

    public static Bitmap ToBitmap(ImageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var bitmap = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(
            new Rectangle(0, 0, frame.Width, frame.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            int rowBytes = frame.Width * 4;
            for (int y = 0; y < frame.Height; y++)
            {
                Marshal.Copy(frame.Bgra, y * rowBytes, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    public static byte[] EncodePng(ImageFrame frame)
    {
        using var bitmap = ToBitmap(frame);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
