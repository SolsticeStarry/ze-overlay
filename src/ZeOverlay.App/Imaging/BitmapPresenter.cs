using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZeOverlay.Core.Imaging;

namespace ZeOverlay.App.Imaging;

/// <summary>ImageFrame(BGRA) → WPF 可显示的 BitmapSource。</summary>
internal static class BitmapPresenter
{
    private static readonly PixelFormat Bgra32 = PixelFormats.Bgra32;

    public static BitmapSource? ToBitmapSource(ImageFrame? frame)
    {
        if (frame is null || frame.Width <= 0 || frame.Height <= 0)
        {
            return null;
        }

        var bitmap = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96,
            96,
            Bgra32,
            null,
            frame.Bgra,
            frame.Stride);

        bitmap.Freeze();
        return bitmap;
    }
}
