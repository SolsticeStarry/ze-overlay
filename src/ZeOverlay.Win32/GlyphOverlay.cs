using System.Drawing;

using ZeOverlay.Shared;

namespace ZeOverlay.Win32;

/// <summary>把字形切分结果画到图上，用于目视校验（而不是只看坐标数字）。</summary>
public static class GlyphOverlayRenderer
{
    public static ImageFrame Draw(ImageFrame frame, IReadOnlyList<Glyph> glyphs)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(glyphs);

        using var bitmap = BitmapCodec.ToBitmap(frame);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        using (var box = new Pen(Color.FromArgb(255, 34, 211, 238), 1))
        using (var index = new SolidBrush(Color.FromArgb(255, 250, 204, 21)))
        using (var font = new Font(FontFamily.GenericMonospace, 7f, FontStyle.Bold))
        {
            for (int i = 0; i < glyphs.Count; i++)
            {
                Glyph glyph = glyphs[i];
                graphics.DrawRectangle(box, glyph.Left, glyph.Top, Math.Max(1, glyph.Width - 1), Math.Max(1, glyph.Height - 1));

                if (i % 5 == 0)
                {
                    graphics.DrawString(i.ToString(), font, index, glyph.Left, Math.Max(0, glyph.Top - 9));
                }
            }
        }

        return BitmapCodec.FromBitmap(bitmap);
    }
}
