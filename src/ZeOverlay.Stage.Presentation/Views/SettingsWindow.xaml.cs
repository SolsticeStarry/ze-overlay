using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using ZeOverlay.Shared;
using ZeOverlay.Infrastructure;

namespace ZeOverlay.Stage.Presentation;

/// <summary>
/// 设置窗口（M5）：关注名单 / 显示与频率 / 热键。
/// 只负责收集与校验，不直接落盘或注册热键——由 Host 在对话框确认后统一应用。
/// </summary>
public partial class SettingsWindow : Window
{
    private List<string> _names;
    private readonly IReadOnlyList<string> _recentNames;

    public SettingsWindow(AppConfig config, WatchlistConfig watchlist, Func<IReadOnlyList<string>> recentNames)
    {
        InitializeComponent();

        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(watchlist);

        Configuration = CloneConfig(config);
        Watchlist = CloneWatchlist(watchlist);

        _names = Watchlist.Names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Take(WatchlistConfig.MaxNames)
            .ToList();
        List<string> configuredOrder = Watchlist.SortOrder
            .Where(_names.Contains)
            .ToList();
        configuredOrder.AddRange(_names.Where(name => !configuredOrder.Contains(name, StringComparer.Ordinal)));
        _names = configuredOrder;

        _recentNames = recentNames();
        RefreshLists();
        LoadIntoControls();
    }

    /// <summary>确认后的 AppConfig（含显示/频率/热键）。</summary>
    public AppConfig Configuration { get; private set; }

    /// <summary>确认后的关注名单。</summary>
    public WatchlistConfig Watchlist { get; private set; }

    private void LoadIntoControls()
    {
        ThresholdTextBox.Text = Watchlist.MatchThreshold.ToString("0.##", CultureInfo.InvariantCulture);

        OverlayConfig overlay = Configuration.Overlay;
        SelectSortMode(overlay.SortMode);
        FontSizeBox.Text = overlay.FontSize.ToString("0.#", CultureInfo.InvariantCulture);
        BackgroundOpacityBox.Text = overlay.BackgroundOpacity.ToString("0.##", CultureInfo.InvariantCulture);
        ShowUsesCheck.IsChecked = overlay.ShowUses;
        ShowPageSlotCheck.IsChecked = overlay.ShowPageSlot;
        ShowSourceMarkCheck.IsChecked = overlay.ShowSourceMark;
        ShowRowNumberCheck.IsChecked = overlay.ShowRowNumber;
        RowSpacingBox.Text = overlay.RowSpacing.ToString("0.#", CultureInfo.InvariantCulture);
        LiveColorBox.Text = overlay.LiveColor;
        ExtrapolatedColorBox.Text = overlay.ExtrapolatedColor;

        CaptureFpsBox.Text = Configuration.Capture.Fps.ToString(CultureInfo.InvariantCulture);
        RecognitionIntervalBox.Text = Configuration.Recognition.IntervalMs.ToString(CultureInfo.InvariantCulture);
        RowDisappearBox.Text = Configuration.Tracking.RowDisappearSeconds.ToString("0.#", CultureInfo.InvariantCulture);

        HotkeyConfig hotkeys = Configuration.Hotkeys;
        HotkeyTogglePreview.Text = hotkeys.TogglePreview;
        HotkeySelectRoi.Text = hotkeys.SelectRoi;
        HotkeySnapshot.Text = hotkeys.Snapshot;
        HotkeyToggleOverlay.Text = hotkeys.ToggleOverlay;
        HotkeyDragOverlay.Text = hotkeys.DragOverlay;
        HotkeyQuit.Text = hotkeys.Quit;
    }

    private void SelectSortMode(string mode)
    {
        int index = 0;
        for (int i = 0; i < SortModeBox.Items.Count; i++)
        {
            if (SortModeBox.Items[i] is ComboBoxItem item
                && string.Equals(item.Tag as string, mode, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        SortModeBox.SelectedIndex = index;
    }

    private string SelectedSortMode =>
        SortModeBox.SelectedItem is ComboBoxItem item && item.Tag is string tag ? tag : "watchlist";

    private void RefreshLists()
    {
        NamesList.ItemsSource = null;
        NamesList.ItemsSource = _names;
        OrderList.ItemsSource = null;
        OrderList.ItemsSource = _names;
        RecentList.ItemsSource = null;
        RecentList.ItemsSource = _recentNames.Where(name => !_names.Contains(name, StringComparer.Ordinal)).ToList();
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        string name = NameTextBox.Text.Trim();
        if (name.Length == 0 || _names.Contains(name, StringComparer.Ordinal)) return;
        if (_names.Count >= WatchlistConfig.MaxNames)
        {
            MessageBox.Show("关注名单最多 10 项。", "ZeOverlay", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _names.Add(name);
        NameTextBox.Clear();
        RefreshLists();
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (NamesList.SelectedItem is string name)
        {
            _names.Remove(name);
            RefreshLists();
        }
    }

    private void OnMoveUpClick(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void OnMoveDownClick(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        int index = OrderList.SelectedIndex;
        int target = index + delta;
        if (index < 0 || target < 0 || target >= _names.Count) return;
        (_names[index], _names[target]) = (_names[target], _names[index]);
        RefreshLists();
        OrderList.SelectedIndex = target;
    }

    private void OnAddRecentClick(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is not string name) return;
        if (_names.Count >= WatchlistConfig.MaxNames)
        {
            MessageBox.Show("关注名单最多 10 项。", "ZeOverlay", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _names.Add(name);
        RefreshLists();
    }

    /// <summary>录入热键：按下组合键即写入该文本框。纯修饰键忽略。</summary>
    private void OnHotkeyCapture(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifierKey(key)) return; // 只按住修饰键时先不动

        var parts = new List<string>();
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        if (parts.Count == 0)
        {
            // 无修饰键的单键容易误触，明确拒绝。
            box.Text = string.Empty;
            e.Handled = true;
            MessageBox.Show("热键至少需要一个修饰键（Ctrl / Alt / Shift / Win）。", "ZeOverlay", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        parts.Add(key.ToString());
        box.Text = string.Join('+', parts);
        e.Handled = true;
    }

    private void OnHotkeyClear(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string name && FindName(name) is TextBox box)
        {
            box.Text = string.Empty;
        }
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin or Key.System;

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(ThresholdTextBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double threshold)
            || threshold is < 0.5 or > 1.0)
        {
            Warn("模糊匹配阈值", "必须是 0.50 到 1.00 之间的数字。", ThresholdTextBox);
            return;
        }

        if (!int.TryParse(FontSizeBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int fontSize)
            || fontSize is < 8 or > 72)
        {
            Warn("字号", "必须是 8 到 72 之间的整数。", FontSizeBox);
            return;
        }

        if (!double.TryParse(BackgroundOpacityBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double opacity)
            || opacity is < 0.0 or > 1.0)
        {
            Warn("背景不透明度", "必须是 0.00 到 1.00 之间的数字。", BackgroundOpacityBox);
            return;
        }

        if (!double.TryParse(RowSpacingBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double rowSpacing)
            || rowSpacing is < -20 or > 40)
        {
            Warn("行间距", "必须是 -20 到 40 之间的数字（像素）。", RowSpacingBox);
            return;
        }

        if (!int.TryParse(CaptureFpsBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int fps)
            || fps is < 1 or > 10)
        {
            Warn("采集频率", "必须是 1 到 10 之间的整数。", CaptureFpsBox);
            return;
        }

        if (!int.TryParse(RecognitionIntervalBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int interval)
            || interval is < 200 or > 5000)
        {
            Warn("识别间隔", "必须是 200 到 5000 之间的整数（毫秒）。", RecognitionIntervalBox);
            return;
        }

        if (!double.TryParse(RowDisappearBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double disappear)
            || disappear is < 0.5 or > 60)
        {
            Warn("行消失超时", "必须是 0.5 到 60 之间的数字（秒）。", RowDisappearBox);
            return;
        }

        if (!TryNormalizeColor(LiveColorBox.Text, out string live))
        {
            Warn("实时颜色", "无法识别，请用 #AARRGGBB 或 #RRGGBB。", LiveColorBox);
            return;
        }

        if (!TryNormalizeColor(ExtrapolatedColorBox.Text, out string extrapolated))
        {
            Warn("外推颜色", "无法识别，请用 #AARRGGBB 或 #RRGGBB。", ExtrapolatedColorBox);
            return;
        }

        Configuration.Overlay.SortMode = SelectedSortMode;
        Configuration.Overlay.FontSize = fontSize;
        Configuration.Overlay.BackgroundOpacity = opacity;
        Configuration.Overlay.ShowUses = ShowUsesCheck.IsChecked == true;
        Configuration.Overlay.ShowPageSlot = ShowPageSlotCheck.IsChecked == true;
        Configuration.Overlay.ShowSourceMark = ShowSourceMarkCheck.IsChecked == true;
        Configuration.Overlay.ShowRowNumber = ShowRowNumberCheck.IsChecked == true;
        Configuration.Overlay.RowSpacing = rowSpacing;
        Configuration.Overlay.LiveColor = live;
        Configuration.Overlay.ExtrapolatedColor = extrapolated;

        Configuration.Capture.Fps = fps;
        Configuration.Recognition.IntervalMs = interval;
        Configuration.Tracking.RowDisappearSeconds = disappear;

        Configuration.Hotkeys.TogglePreview = HotkeyTogglePreview.Text.Trim();
        Configuration.Hotkeys.SelectRoi = HotkeySelectRoi.Text.Trim();
        Configuration.Hotkeys.Snapshot = HotkeySnapshot.Text.Trim();
        Configuration.Hotkeys.ToggleOverlay = HotkeyToggleOverlay.Text.Trim();
        Configuration.Hotkeys.DragOverlay = HotkeyDragOverlay.Text.Trim();
        Configuration.Hotkeys.Quit = HotkeyQuit.Text.Trim();

        Watchlist = new WatchlistConfig
        {
            Version = Watchlist.Version,
            Names = _names.ToList(),
            SortOrder = _names.ToList(),
            MatchThreshold = threshold,
        };

        DialogResult = true;
    }

    private static void Warn(string field, string message, Control focus)
    {
        MessageBox.Show($"{field}：{message}", "ZeOverlay", MessageBoxButton.OK, MessageBoxImage.Warning);
        focus.Focus();
    }

    private static bool TryNormalizeColor(string? text, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            if (ColorConverter.ConvertFromString(text.Trim()) is Color color)
            {
                normalized = color.ToString(CultureInfo.InvariantCulture);
                return true;
            }
        }
        catch (FormatException)
        {
        }
        catch (NotSupportedException)
        {
        }

        return false;
    }

    private static AppConfig CloneConfig(AppConfig source)
    {
        // 深拷贝一份，取消对话框时不污染运行中的配置。
        return new AppConfig
        {
            Version = source.Version,
            Capture = new CaptureConfig
            {
                Fps = source.Capture.Fps,
                MonitorIndex = source.Capture.MonitorIndex,
                CaptureEnabled = source.Capture.CaptureEnabled,
            },
            Roi = source.Roi,
            Hotkeys = new HotkeyConfig
            {
                TogglePreview = source.Hotkeys.TogglePreview,
                SelectRoi = source.Hotkeys.SelectRoi,
                Snapshot = source.Hotkeys.Snapshot,
                Quit = source.Hotkeys.Quit,
                ToggleOverlay = source.Hotkeys.ToggleOverlay,
                DragOverlay = source.Hotkeys.DragOverlay,
            },
            Overlay = new OverlayConfig
            {
                Shown = source.Overlay.Shown,
                X = source.Overlay.X,
                Y = source.Overlay.Y,
                FontSize = source.Overlay.FontSize,
                ShowUses = source.Overlay.ShowUses,
                ExtrapolatedColor = source.Overlay.ExtrapolatedColor,
                LiveColor = source.Overlay.LiveColor,
                SortMode = source.Overlay.SortMode,
                ShowPageSlot = source.Overlay.ShowPageSlot,
                ShowSourceMark = source.Overlay.ShowSourceMark,
                ShowRowNumber = source.Overlay.ShowRowNumber,
                RowSpacing = source.Overlay.RowSpacing,
                BackgroundOpacity = source.Overlay.BackgroundOpacity,
            },
            Storage = source.Storage,
            Recognition = new RecognitionConfig
            {
                IntraOpThreads = source.Recognition.IntraOpThreads,
                AllowSpinning = source.Recognition.AllowSpinning,
                ExecutionProvider = source.Recognition.ExecutionProvider,
                FixedInputWidth = source.Recognition.FixedInputWidth,
                BatchSize = source.Recognition.BatchSize,
                IntervalMs = source.Recognition.IntervalMs,
            },
            Tracking = new TrackingConfig
            {
                RowDisappearSeconds = source.Tracking.RowDisappearSeconds,
            },
        };
    }

    private static WatchlistConfig CloneWatchlist(WatchlistConfig source) => new()
    {
        Version = source.Version,
        Names = source.Names.ToList(),
        SortOrder = source.SortOrder.ToList(),
        MatchThreshold = source.MatchThreshold,
    };
}
