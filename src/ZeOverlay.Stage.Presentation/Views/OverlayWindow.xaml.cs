using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

using ZeOverlay.Shared;
using ZeOverlay.Infrastructure;

namespace ZeOverlay.Stage.Presentation;

/// <summary>
/// 穿透叠加窗口（PLAN 第 8.1 节）：置顶、逐像素透明、**点击穿透**，不遮挡游戏操作。
///
/// 点击穿透靠 `WS_EX_TRANSPARENT`；进入拖动模式时临时去掉它（穿透窗口没法直接拖），
/// 拖完再恢复。窗口位置持久化到 config.json。
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;

    private bool _dragMode;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyClickThrough(!_dragMode);
    }

    public bool IsDragMode => _dragMode;

    /// <summary>进入/退出拖动模式。返回进入后的状态。</summary>
    public bool ToggleDragMode()
    {
        _dragMode = !_dragMode;
        ApplyClickThrough(!_dragMode);
        DragHint.Visibility = _dragMode ? Visibility.Visible : Visibility.Collapsed;

        if (_dragMode)
        {
            Activate();
        }

        return _dragMode;
    }

    public void ExitDragMode()
    {
        if (_dragMode)
        {
            ToggleDragMode();
        }
    }

    /// <summary>按跟踪结果重建显示。名单为空时全部显示。</summary>
    public void UpdateEntries(IReadOnlyList<TrackerEntryView> entries, OverlayConfig config, bool hasWatchlist)
    {
        FontSize = config.FontSize;

        // 背景不透明度可配（0 = 完全透明，只剩文字）。
        byte alpha = (byte)Math.Round(Math.Clamp(config.BackgroundOpacity, 0.0, 1.0) * 255);
        Frame.Background = new SolidColorBrush(Color.FromArgb(alpha, 0x10, 0x10, 0x14));

        Rows.Children.Clear();

        if (entries.Count == 0)
        {
            Rows.Children.Add(new TextBlock
            {
                Text = hasWatchlist ? "（名单内暂无可显示的神器）" : "（暂无可显示的神器）",
                Foreground = new SolidColorBrush(Color.FromArgb(180, 200, 200, 200)),
                FontSize = config.FontSize,
            });

            return;
        }

        // 颜色统一：正文不再按“实时 / 外推”换色；外推只用最左侧的 ▲ 标记区分。
        Brush body = Parse(config.LiveColor);
        Brush marker = Parse(config.ExtrapolatedColor);
        double halfSpacing = config.RowSpacing / 2.0;

        for (int i = 0; i < entries.Count; i++)
        {
            TrackerEntryView entry = entries[i];

            string status = entry.State switch
            {
                ArtifactState.Ready => "[R]",
                ArtifactState.Cooling => $"[{entry.CooldownSeconds}]",
                _ => "[?]",
            };

            string uses = config.ShowUses && entry.UsesRemaining is { } remaining && entry.UsesTotal is { } total
                ? $"{remaining}/{total}"
                : string.Empty;

            var block = new TextBlock
            {
                FontSize = config.FontSize,
                Foreground = body,
                Margin = new Thickness(0, halfSpacing, 0, halfSpacing),
                // 固定行高：避免放大的 ▲ 把每行撑高；行间距统一由 RowSpacing 控制。
                LineHeight = config.FontSize * 1.5,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            };

            // 最左：为未扫描到标记 ▲ 预留固定宽度。
            // 关键：实时行也用**同字形同字号**（只是透明），这样无论有没有 ▲，正文起点都一致，不会左右跳动。
            if (config.ShowSourceMark)
            {
                block.Inlines.Add(new System.Windows.Documents.Run("▲ ")
                {
                    Foreground = entry.Source == EntrySource.Live ? Brushes.Transparent : marker,
                    FontWeight = FontWeights.Bold,
                    FontSize = config.FontSize * 1.5,
                });
            }

            // 行号：名称未带数字标号时，按显示顺序从 1 编号；右对齐占 2 位，避免宽度跳动。
            if (config.ShowRowNumber && !RowLabels.HasNumberLabel(entry.ArtifactName))
            {
                block.Inlines.Add(new System.Windows.Documents.Run($"{i + 1,2}. ")
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                    FontSize = config.FontSize * 0.9,
                });
            }

            if (config.ShowPageSlot)
            {
                block.Inlines.Add(new System.Windows.Documents.Run($"#{(int)entry.Page}-{entry.Slot:00}  ")
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(113, 113, 122)),
                    FontSize = config.FontSize * 0.85,
                });
            }

            block.Inlines.Add(new System.Windows.Documents.Run(entry.ArtifactName));
            block.Inlines.Add(new System.Windows.Documents.Run(" " + status)
            {
                Foreground = entry.State == ArtifactState.Cooling
                    ? new SolidColorBrush(Color.FromRgb(251, 146, 60))
                    : new SolidColorBrush(Color.FromRgb(74, 222, 128)),
            });

            if (uses.Length > 0)
            {
                block.Inlines.Add(new System.Windows.Documents.Run(" " + uses)
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                });
            }

            Rows.Children.Add(block);
        }
    }

    public void SetPosition(double x, double y)
    {
        Left = x;
        Top = y;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (_dragMode)
        {
            DragMove();
        }
    }

    /// <summary>
    /// 开关点击穿透。`WS_EX_TRANSPARENT` 让鼠标事件穿过本窗口。
    /// 拖动模式必须关掉它，否则收不到鼠标事件。
    /// </summary>
    private void ApplyClickThrough(bool enabled)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        int style = GetWindowLong(handle, GwlExStyle);
        style |= WsExToolWindow;

        style = enabled
            ? style | WsExTransparent
            : style & ~WsExTransparent;

        SetWindowLong(handle, GwlExStyle, style);
    }

    private static Brush Parse(string text)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(text));
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return Brushes.White;
        }
        catch (NotSupportedException)
        {
            return Brushes.White;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
