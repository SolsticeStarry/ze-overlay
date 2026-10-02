using System.Drawing;

using ZeOverlay.Shared;

namespace ZeOverlay.Win32;

/// <summary>
/// 把行剖分结果画到图上，用于**目视校验**切分是否正确（而不是只看 JSON 数字）。
/// </summary>
public static class BandOverlayRenderer
{
    public static ImageFrame DrawBands(ImageFrame frame, IReadOnlyList<RowBand> bands)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(bands);

        using var bitmap = BitmapCodec.ToBitmap(frame);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        using (var pen = new Pen(Color.FromArgb(255, 34, 211, 238), 2))
        using (var font = new Font(FontFamily.GenericMonospace, 11f, FontStyle.Bold))
        using (var brush = new SolidBrush(Color.FromArgb(255, 250, 204, 21)))
        using (var backdrop = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
        {
            foreach (RowBand band in bands)
            {
                graphics.DrawRectangle(pen, band.Left, band.Top, Math.Max(1, band.Width - 1), Math.Max(1, band.Height - 1));

                string label = band.Index.ToString();
                SizeF size = graphics.MeasureString(label, font);
                float labelX = Math.Max(0, band.Left - size.Width - 4);
                float labelY = band.Top;

                graphics.FillRectangle(backdrop, labelX, labelY, size.Width, size.Height);
                graphics.DrawString(label, font, brush, labelX, labelY);
            }
        }

        return BitmapCodec.FromBitmap(bitmap);
    }
}
