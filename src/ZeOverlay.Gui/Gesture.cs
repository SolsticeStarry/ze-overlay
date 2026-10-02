using System.Windows.Input;

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

/// <summary>热键手势（如 "Ctrl+Alt+R"）：修饰键 + 虚拟键码。</summary>
public readonly record struct HotkeyGesture(uint Modifiers, uint VirtualKey, string Text)
{
    private static readonly Dictionary<string, uint> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CTRL"] = NativeInput.MOD_CONTROL,
        ["CONTROL"] = NativeInput.MOD_CONTROL,
        ["ALT"] = NativeInput.MOD_ALT,
        ["SHIFT"] = NativeInput.MOD_SHIFT,
        ["WIN"] = NativeInput.MOD_WIN,
        ["WINDOWS"] = NativeInput.MOD_WIN,
    };

    public static bool TryParse(string? text, out HotkeyGesture gesture, out string? error)
    {
        gesture = default;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "热键为空。";
            return false;
        }

        string[] tokens = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            error = $"无法解析热键 '{text}'。";
            return false;
        }

        uint modifiers = 0;
        for (int i = 0; i < tokens.Length - 1; i++)
        {
            if (!ModifierNames.TryGetValue(tokens[i], out uint modifier))
            {
                error = $"未知修饰键 '{tokens[i]}'（来自 '{text}'）。";
                return false;
            }

            modifiers |= modifier;
        }

        string keyName = tokens[^1];
        if (!Enum.TryParse(keyName, ignoreCase: true, out Key key) || key == Key.None)
        {
            error = $"未知按键 '{keyName}'（来自 '{text}'）。";
            return false;
        }

        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey <= 0)
        {
            error = $"按键 '{keyName}' 无法映射为虚拟键码。";
            return false;
        }

        gesture = new HotkeyGesture(modifiers, (uint)virtualKey, text);
        return true;
    }
}
