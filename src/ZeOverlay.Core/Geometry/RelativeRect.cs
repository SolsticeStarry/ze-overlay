using System.Globalization;

namespace ZeOverlay.Core.Geometry;

/// <summary>
/// 相对某个目标窗口的比例矩形（0..1）。
/// PLAN 第 9 节的「自动跟随」：窗口移动/缩放时按比例重映射列表区域，
/// 因此 ROI 除绝对像素外必须同时保存为窗口相对比例。
/// </summary>
public readonly record struct RelativeRect(double X, double Y, double Width, double Height)
{
    public static readonly RelativeRect Empty = new(0, 0, 0, 0);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static RelativeRect FromAbsolute(PixelRect roi, PixelRect window)
    {
        if (window.Width <= 0 || window.Height <= 0)
        {
            return Empty;
        }

        return new RelativeRect(
            (roi.X - window.X) / (double)window.Width,
            (roi.Y - window.Y) / (double)window.Height,
            roi.Width / (double)window.Width,
            roi.Height / (double)window.Height);
    }

    public PixelRect ToAbsolute(PixelRect window)
    {
        if (IsEmpty || window.Width <= 0 || window.Height <= 0)
        {
            return PixelRect.Empty;
        }

        return new PixelRect(
            window.X + (int)Math.Round(X * window.Width),
            window.Y + (int)Math.Round(Y * window.Height),
            Math.Max(1, (int)Math.Round(Width * window.Width)),
            Math.Max(1, (int)Math.Round(Height * window.Height)));
    }

    public override string ToString()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{X:0.000000},{Y:0.000000},{Width:0.000000},{Height:0.000000}");

    public static RelativeRect Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Empty;
        }

        string[] parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
        {
            throw new FormatException($"相对矩形文本应为 x,y,w,h，实际为 '{text}'。");
        }

        return new RelativeRect(
            double.Parse(parts[0], CultureInfo.InvariantCulture),
            double.Parse(parts[1], CultureInfo.InvariantCulture),
            double.Parse(parts[2], CultureInfo.InvariantCulture),
            double.Parse(parts[3], CultureInfo.InvariantCulture));
    }
}
