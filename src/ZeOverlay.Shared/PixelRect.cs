using System.Globalization;

namespace ZeOverlay.Shared;

/// <summary>
/// 屏幕物理像素坐标系下的矩形（左上角 + 宽高）。
/// 全项目统一使用物理像素，避免 DPI 虚拟化带来的坐标漂移。
/// </summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public static readonly PixelRect Empty = new(0, 0, 0, 0);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public int Right => X + Width;

    public int Bottom => Y + Height;

    public int Area => IsEmpty ? 0 : Width * Height;

    public bool Contains(int px, int py) => px >= X && px < Right && py >= Y && py < Bottom;

    /// <summary>把可能为负的宽高规整为「左上角 + 正宽高」，用于拖拽框选的反向拖动。</summary>
    public PixelRect Normalized()
    {
        int x = Width < 0 ? X + Width : X;
        int y = Height < 0 ? Y + Height : Y;
        return new PixelRect(x, y, Math.Abs(Width), Math.Abs(Height));
    }

    public PixelRect Intersect(PixelRect other)
    {
        int x = Math.Max(X, other.X);
        int y = Math.Max(Y, other.Y);
        int right = Math.Min(Right, other.Right);
        int bottom = Math.Min(Bottom, other.Bottom);
        return right <= x || bottom <= y ? Empty : new PixelRect(x, y, right - x, bottom - y);
    }

    public PixelRect Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);

    public bool SimilarTo(PixelRect other, int tolerance)
        => Math.Abs(X - other.X) <= tolerance
        && Math.Abs(Y - other.Y) <= tolerance
        && Math.Abs(Width - other.Width) <= tolerance
        && Math.Abs(Height - other.Height) <= tolerance;

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{X},{Y},{Width},{Height}");

    public static PixelRect Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Empty;
        }

        string[] parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
        {
            throw new FormatException($"矩形文本应为 x,y,w,h，实际为 '{text}'。");
        }

        return new PixelRect(
            int.Parse(parts[0], CultureInfo.InvariantCulture),
            int.Parse(parts[1], CultureInfo.InvariantCulture),
            int.Parse(parts[2], CultureInfo.InvariantCulture),
            int.Parse(parts[3], CultureInfo.InvariantCulture));
    }
}
