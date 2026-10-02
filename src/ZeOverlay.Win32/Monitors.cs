using System.Runtime.InteropServices;

using ZeOverlay.Shared;

namespace ZeOverlay.Win32;

/// <summary>一块显示器的物理像素信息。</summary>
public sealed record MonitorInfo(
    int Index,
    string DeviceName,
    PixelRect Bounds,
    PixelRect WorkArea,
    bool IsPrimary,
    uint Dpi,
    double Scale);

/// <summary>
/// 显示器枚举与 DPI 查询。PLAN 第 9 节：强制 per-monitor DPI 感知，统一按物理像素换算。
/// 本机实测环境为单显示器 1920x1080 @100%，但代码不做单屏假设。
/// </summary>
public static class MonitorService
{
    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        var monitors = new List<MonitorInfo>();
        int index = 0;

        NativeMethods.MonitorEnumProc callback = (IntPtr hMonitor, IntPtr _, ref NativeMethods.RECT _, IntPtr _) =>
        {
            var info = new NativeMethods.MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
                szDevice = string.Empty,
            };

            if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
            {
                uint dpiX = 96;
                uint dpiY = 96;
                if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dx, out uint dy) == 0)
                {
                    dpiX = dx;
                    dpiY = dy;
                }

                // 96 dpi == 100% 缩放。
                double scale = dpiX / 96.0;

                monitors.Add(new MonitorInfo(
                    index++,
                    string.IsNullOrEmpty(info.szDevice) ? $"DISPLAY{index}" : info.szDevice,
                    new PixelRect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Width, info.rcMonitor.Height),
                    new PixelRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Width, info.rcWork.Height),
                    (info.dwFlags & 1) != 0,
                    dpiX,
                    scale));
            }

            return true;
        };

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

        if (monitors.Count == 0)
        {
            // 极端兜底：至少给出主屏，避免上层空集崩溃。
            monitors.Add(new MonitorInfo(0, "PRIMARY", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1080), true, 96, 1.0));
        }

        return monitors;
    }

    public static MonitorInfo Primary()
        => Enumerate().FirstOrDefault(m => m.IsPrimary) ?? Enumerate()[0];

    /// <summary>返回包含指定屏幕坐标点的显示器（无匹配时返回最近的）。</summary>
    public static MonitorInfo FromPoint(int x, int y)
    {
        var pt = new NativeMethods.POINT { X = x, Y = y };
        IntPtr handle = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (handle == IntPtr.Zero)
        {
            return Primary();
        }

        var info = new NativeMethods.MONITORINFOEX
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
            szDevice = string.Empty,
        };

        if (!NativeMethods.GetMonitorInfo(handle, ref info))
        {
            return Primary();
        }

        var bounds = new PixelRect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Width, info.rcMonitor.Height);

        uint dpi = 96;
        if (NativeMethods.GetDpiForMonitor(handle, NativeMethods.MDT_EFFECTIVE_DPI, out uint dx, out _) == 0)
        {
            dpi = dx;
        }

        return new MonitorInfo(
            -1,
            string.IsNullOrEmpty(info.szDevice) ? "DISPLAY" : info.szDevice,
            bounds,
            new PixelRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Width, info.rcWork.Height),
            (info.dwFlags & 1) != 0,
            dpi,
            dpi / 96.0);
    }
}
