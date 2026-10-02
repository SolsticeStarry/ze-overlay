using System.Runtime.InteropServices;
using ZeOverlay.Core.Capture;
using ZeOverlay.Core.Geometry;
using ZeOverlay.Core.Imaging;

namespace ZeOverlay.Platform.Windows;

/// <summary>
/// GDI BitBlt 截屏后端：GetDC(NULL) → CreateCompatibleDC → CreateDIBSection → BitBlt → 读内存。
///
/// 为什么不用 System.Drawing 的 Graphics.CopyFromScreen：真机实测（400x600 ROI）
/// 走 GDI+ 的路径中位数约 12–14 ms，**高于 PLAN 第 11 节 &lt;5 ms 基准**；
/// 瓶颈在 GDI+ 的位图包装与逐行 LockBits，而不是 BitBlt 本身。
/// 这里直接持有 DIB 段，Blt 结果即内存，只需一次拷贝。
///
/// 局限：对独占全屏 / 部分硬件叠加场景可能返回黑屏；CS2 为无边框窗口时由 DWM 合成，通常可用。
/// 若真机实测失败，按 PLAN 第 10 节替换为 WGC / 桌面复制后端（接口不变）。
/// </summary>
public sealed class GdiScreenCapture : IScreenCapture
{
    private const uint SRCCOPY = 0x00CC0020;
    private const uint DIB_RGB_COLORS = 0;
    private const int BI_RGB = 0;

    private readonly object _gate = new();
    private IntPtr _memoryDc = IntPtr.Zero;
    private IntPtr _dib = IntPtr.Zero;
    private IntPtr _previousObject = IntPtr.Zero;
    private IntPtr _bits = IntPtr.Zero;
    private int _surfaceWidth;
    private int _surfaceHeight;
    private bool _disposed;

    public string Name => "GDI BitBlt(DIB)";

    public bool TryCapture(PixelRect region, out ImageFrame? frame, out string? error)
    {
        frame = null;
        error = null;

        if (region.IsEmpty)
        {
            error = "ROI 为空，无法截屏。";
            return false;
        }

        if (_disposed)
        {
            error = "截屏后端已释放。";
            return false;
        }

        lock (_gate)
        {
            IntPtr screenDc = IntPtr.Zero;
            try
            {
                if (!EnsureSurface(region.Width, region.Height, out string? surfaceError))
                {
                    error = surfaceError;
                    return false;
                }

                screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero)
                {
                    error = "GetDC(NULL) 失败。";
                    return false;
                }

                if (!BitBlt(_memoryDc, 0, 0, region.Width, region.Height, screenDc, region.X, region.Y, SRCCOPY))
                {
                    error = $"BitBlt 失败（Win32 错误 {Marshal.GetLastWin32Error()}）。";
                    return false;
                }

                byte[] buffer = new byte[region.Width * region.Height * 4];
                Marshal.Copy(_bits, buffer, 0, buffer.Length);

                // BitBlt 不写 alpha，统一补 255，否则 WPF 显示与 PNG 都会变透明。
                for (int i = 3; i < buffer.Length; i += 4)
                {
                    buffer[i] = 255;
                }

                frame = ImageFrame.FromBgra(region.Width, region.Height, buffer);
                return true;
            }
            catch (OutOfMemoryException ex)
            {
                error = $"内存不足：{ex.Message}";
                return false;
            }
            finally
            {
                if (screenDc != IntPtr.Zero)
                {
                    ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ReleaseSurface();
        }
    }

    private bool EnsureSurface(int width, int height, out string? error)
    {
        error = null;

        if (_memoryDc != IntPtr.Zero && _surfaceWidth == width && _surfaceHeight == height)
        {
            return true;
        }

        ReleaseSurface();

        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            error = "GetDC(NULL) 失败，无法创建截屏表面。";
            return false;
        }

        try
        {
            _memoryDc = CreateCompatibleDC(screenDc);

            var header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                // 负高度 = top-down，内存顺序与 ImageFrame 的自上而下约定一致。
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = BI_RGB,
            };

            var info = new BitmapInfo { Header = header };

            _dib = CreateDIBSection(screenDc, ref info, DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero || _bits == IntPtr.Zero)
            {
                ReleaseSurface();
                error = "CreateDIBSection 失败。";
                return false;
            }

            _previousObject = SelectObject(_memoryDc, _dib);
            _surfaceWidth = width;
            _surfaceHeight = height;
            return true;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void ReleaseSurface()
    {
        if (_memoryDc != IntPtr.Zero && _previousObject != IntPtr.Zero)
        {
            SelectObject(_memoryDc, _previousObject);
        }

        _previousObject = IntPtr.Zero;

        if (_dib != IntPtr.Zero)
        {
            DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }

        if (_memoryDc != IntPtr.Zero)
        {
            DeleteDC(_memoryDc);
            _memoryDc = IntPtr.Zero;
        }

        _bits = IntPtr.Zero;
        _surfaceWidth = 0;
        _surfaceHeight = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc,
        ref BitmapInfo pbmi,
        uint usage,
        out IntPtr ppvBits,
        IntPtr hSection,
        uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int cx, int cy, IntPtr hdcSrc, int xSrc, int ySrc, uint rop);
}
