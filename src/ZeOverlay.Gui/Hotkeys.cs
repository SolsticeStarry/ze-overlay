using System.Windows.Interop;

using ZeOverlay.Shared;
using ZeOverlay.Infrastructure;
using ZeOverlay.Win32;
using ZeOverlay.Stage.ScreenCapture;
using ZeOverlay.Stage.RowAnalysis;
using ZeOverlay.Stage.GlyphSegmentation;
using ZeOverlay.Stage.Recognition;
using ZeOverlay.Stage.Parsing;
using ZeOverlay.Stage.Tracking;
using ZeOverlay.Stage.Matching;
using ZeOverlay.Stage.Presentation;

namespace ZeOverlay.Gui;

/// <summary>
/// 全局热键注册（PLAN 第 1.3 节：需要开关叠加 / 呼出设置 / 拖动模式热键）。
/// 用 WPF 的 HwndSource 挂 WM_HOTKEY，不额外创建消息窗口。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, (HotkeyGesture Gesture, Action Callback)> _handlers = new();
    private int _nextId = 0x4A00;
    private bool _disposed;

    public HotkeyManager(HwndSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _source.AddHook(WndProc);
    }

    /// <summary>注册失败（手势非法或被占用）时返回 false 并给出原因，调用方决定是否致命。</summary>
    public bool TryRegister(string gestureText, Action callback, out string? error)
    {
        ArgumentNullException.ThrowIfNull(callback);

        error = null;

        if (!HotkeyGesture.TryParse(gestureText, out HotkeyGesture gesture, out error))
        {
            return false;
        }

        int id = _nextId++;
        if (!NativeInput.RegisterHotKey(_source.Handle, id, gesture.Modifiers | NativeInput.MOD_NOREPEAT, gesture.VirtualKey))
        {
            error = $"注册热键 {gesture.Text} 失败（可能已被其他程序占用）。";
            return false;
        }

        _handlers[id] = (gesture, callback);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.RemoveHook(WndProc);

        foreach (int id in _handlers.Keys)
        {
            NativeInput.UnregisterHotKey(_source.Handle, id);
        }

        _handlers.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeInput.WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out var entry))
        {
            handled = true;
            entry.Callback();
        }

        return IntPtr.Zero;
    }
}
