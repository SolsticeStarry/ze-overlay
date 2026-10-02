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

        Brush live = Parse(config.LiveColor);
        Brush extrapolated = Parse(config.ExtrapolatedColor);

        foreach (TrackerEntryView entry in entries)
        {
            string status = entry.State switch
            {
                ArtifactState.Ready => "[R]",
                ArtifactState.Cooling => $"[{entry.CooldownSeconds}]",
                _ => "[?]",
            };

            string uses = config.ShowUses && entry.UsesRemaining is { } remaining && entry.UsesTotal is { } total
                ? $"{remaining}/{total}"
                : string.Empty;

            // 来源区分：实时用正常色，外推用另一种颜色（PLAN 第 8.1 节）。
            var block = new TextBlock
            {
                FontSize = config.FontSize,
                Foreground = entry.Source == EntrySource.Live ? live : extrapolated,
                Margin = new Thickness(0, 1, 0, 1),
            };

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

            if (config.ShowSourceMark && entry.Source != EntrySource.Live)
            {
                block.Inlines.Add(new System.Windows.Documents.Run("  ~")
                {
                    Foreground = extrapolated,
                    FontSize = config.FontSize * 0.85,
                });
            }

            if (config.ShowPageSlot)
            {
                var prefix = new System.Windows.Documents.Run($"#{(int)entry.Page}-{entry.Slot:00}  ")
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(113, 113, 122)),
                    FontSize = config.FontSize * 0.85,
                };
                block.Inlines.InsertBefore(block.Inlines.FirstInline, prefix);
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
