using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

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

public sealed partial class Host
{
    // Host 的「全局热键」部分（partial）。
    private void RegisterHotkeys()
    {
        IntPtr handle = new WindowInteropHelper(_preview!).Handle;
        HwndSource? source = HwndSource.FromHwnd(handle);
        if (source is null)
        {
            _log?.Error("无法获取窗口消息源，全局热键不可用（按钮仍可用）。");
            _notice = "全局热键不可用，请用界面按钮操作。";
            return;
        }

        _hotkeys = new HotkeyManager(source);

        // 首选手势取自 config.json；被系统内其他程序占用时按候选顺序回退，
        // 实测本机 Ctrl+Alt+R 已被占用，故必须有回退，否则会丢掉「重新框选」入口。
        RegisterWithFallback(LabelTogglePreview, _config.Hotkeys.TogglePreview, ["Ctrl+Shift+O", "Ctrl+Alt+F9"], TogglePreview);
        RegisterWithFallback(LabelSelectRoi, _config.Hotkeys.SelectRoi, ["Ctrl+Shift+R", "Ctrl+Alt+F2"], SelectRoi);
        RegisterWithFallback(LabelSnapshot, _config.Hotkeys.Snapshot, ["Ctrl+Shift+S", "Ctrl+Alt+F3"], Snapshot);
        RegisterWithFallback(LabelQuit, _config.Hotkeys.Quit, ["Ctrl+Shift+Q", "Ctrl+Alt+F12"], Quit);
        RegisterWithFallback(LabelToggleOverlay, _config.Hotkeys.ToggleOverlay, ["Ctrl+Shift+H", "Ctrl+Alt+F5"], ToggleOverlay);
        RegisterWithFallback(LabelDragOverlay, _config.Hotkeys.DragOverlay, ["Ctrl+Shift+D", "Ctrl+Alt+F6"], ToggleOverlayDragMode);

        // 提示必须用**实际生效**的手势，否则用户按提示操作会没反应。
        _preview?.SetHotkeyHints(HotkeyHint(LabelSelectRoi), HotkeyHint(LabelSnapshot));
    }

    /// <summary>设置变更后重新注册全部热键（先释放旧注册，避免手势残留）。</summary>
    private void ReloadHotkeys()
    {
        _hotkeys?.Dispose();
        _hotkeys = null;
        _resolvedHotkeys.Clear();
        RegisterHotkeys();
        _log?.Info("热键已按新设置重新注册：" + DescribeHotkeys());
    }

    /// <summary>取某个动作实际生效的热键；全部被占用时给出可读说明。</summary>
    private string HotkeyHint(string label)
        => _resolvedHotkeys.TryGetValue(label, out string? gesture) && !gesture.StartsWith('(')
            ? gesture
            : "界面按钮";

    private void RegisterWithFallback(string label, string preferred, string[] fallbacks, Action action)
    {
        foreach (string gesture in new[] { preferred }.Concat(fallbacks))
        {
            if (string.IsNullOrWhiteSpace(gesture))
            {
                continue;
            }

            if (_hotkeys!.TryRegister(gesture, action, out string? error))
            {
                _resolvedHotkeys[label] = gesture;
                _log?.Info($"热键已注册：{label} = {gesture}");
                return;
            }

            _log?.Warn($"热键候选被占用：{label} = {gesture}；{error}");
        }

        _resolvedHotkeys[label] = "(全部被占用)";
        _log?.Warn($"热键 {label} 的所有候选均不可用；界面按钮仍然可用。");
    }

    private string DescribeHotkeys()
    {
        if (_resolvedHotkeys.Count == 0)
        {
            return "(不可用，请用界面按钮)";
        }

        return string.Join(
            "  ",
            _resolvedHotkeys.Select(pair => $"{pair.Key}={pair.Value}"));
    }

}
