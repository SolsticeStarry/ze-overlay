using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

using ZeOverlay.Shared;

namespace ZeOverlay.Win32;

/// <summary>一个顶层窗口的几何与归属信息。</summary>
public sealed record WindowInfo(
    IntPtr Handle,
    string Title,
    string ProcessName,
    int ProcessId,
    PixelRect Bounds)
{
    public bool IsVisibleAndNormal => !Bounds.IsEmpty;
}

/// <summary>
/// 目标窗口查找与矩形获取。PLAN 第 9 节「自动跟随」依赖此处的窗口矩形。
/// 注意：这里只读取窗口几何/标题，不做任何输入注入。
/// </summary>
public static class WindowLocator
{
    public static IReadOnlyList<WindowInfo> EnumerateVisibleWindows()
    {
        var result = new List<WindowInfo>();

        NativeMethods.EnumWindowsProc callback = (IntPtr hWnd, IntPtr _) =>
        {
            if (NativeMethods.IsWindowVisible(hWnd) && !NativeMethods.IsIconic(hWnd))
            {
                string title = GetTitle(hWnd);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    result.Add(new WindowInfo(
                        hWnd,
                        title,
                        GetProcessName(hWnd),
                        GetProcessId(hWnd),
                        GetBounds(hWnd)));
                }
            }

            return true;
        };

        NativeMethods.EnumWindows(callback, IntPtr.Zero);
        return result;
    }

    /// <summary>按进程名（不含 .exe）与标题子串查找窗口；两者为空则返回 null。</summary>
    public static WindowInfo? Find(string? processName, string? titleSubstring)
    {
        if (string.IsNullOrWhiteSpace(processName) && string.IsNullOrWhiteSpace(titleSubstring))
        {
            return null;
        }

        foreach (WindowInfo window in EnumerateVisibleWindows())
        {
            bool processOk = string.IsNullOrWhiteSpace(processName)
                || string.Equals(window.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
            bool titleOk = string.IsNullOrWhiteSpace(titleSubstring)
                || window.Title.Contains(titleSubstring, StringComparison.OrdinalIgnoreCase);

            if (processOk && titleOk)
            {
                return window;
            }
        }

        return null;
    }

    /// <summary>
    /// 取窗口可见边框。优先 DWM 扩展边框（排除无边框窗口的透明阴影区），
    /// 失败则退回 GetWindowRect。
    /// </summary>
    public static PixelRect GetBounds(IntPtr hWnd)
    {
        if (NativeMethods.DwmGetWindowAttribute(
                hWnd,
                NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out NativeMethods.RECT dwmRect,
                Marshal.SizeOf<NativeMethods.RECT>()) == 0
            && dwmRect.Width > 0
            && dwmRect.Height > 0)
        {
            return new PixelRect(dwmRect.Left, dwmRect.Top, dwmRect.Width, dwmRect.Height);
        }

        if (NativeMethods.GetWindowRect(hWnd, out NativeMethods.RECT rect) && rect.Width > 0 && rect.Height > 0)
        {
            return new PixelRect(rect.Left, rect.Top, rect.Width, rect.Height);
        }

        return PixelRect.Empty;
    }

    /// <summary>当前前台窗口（用于框选时自动记下「用户正在看的目标程序」）。</summary>
    public static WindowInfo? Foreground()
    {
        IntPtr hWnd = NativeMethods.GetForegroundWindow();
        if (hWnd == IntPtr.Zero)
        {
            return null;
        }

        return new WindowInfo(
            hWnd,
            GetTitle(hWnd),
            GetProcessName(hWnd),
            GetProcessId(hWnd),
            GetBounds(hWnd));
    }

    public static string GetTitle(IntPtr hWnd)
    {
        int length = NativeMethods.GetWindowTextLength(hWnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        char[] buffer = new char[length + 1];
        int copied = NativeMethods.GetWindowText(hWnd, buffer, buffer.Length);
        return copied <= 0 ? string.Empty : new string(buffer, 0, copied);
    }

    public static string GetProcessName(IntPtr hWnd)
    {
        try
        {
            int pid = GetProcessId(hWnd);
            if (pid <= 0)
            {
                return string.Empty;
            }

            using Process process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    public static int GetProcessId(IntPtr hWnd)
    {
        NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
        return (int)pid;
    }

    /// <summary>把窗口标题/进程写成单行摘要，便于日志排查。</summary>
    public static string Describe(WindowInfo? window)
    {
        if (window is null)
        {
            return "(未找到目标窗口)";
        }

        var sb = new StringBuilder();
        sb.Append(window.ProcessName).Append(".exe  pid=").Append(window.ProcessId);
        sb.Append("  rect=").Append(window.Bounds);
        sb.Append("  title=\"").Append(window.Title).Append('"');
        return sb.ToString();
    }
}
