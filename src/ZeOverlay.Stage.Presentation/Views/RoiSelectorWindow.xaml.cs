using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

using ZeOverlay.Shared;
using ZeOverlay.Win32;

namespace ZeOverlay.Stage.Presentation;

/// <summary>
/// PLAN 第 9 节「实时覆盖层框选」：全屏覆盖层上直接框出整个列表区域，普通权限。
///
/// 关键实现选择：鼠标坐标取自**原始窗口消息 lParam**（客户区物理像素），
/// 而不是 WPF 事件的 DIP 坐标或事后再调用 GetCursorPos()。
/// 实测证明后者会漂移——UI 线程繁忙时读到的已是「几百毫秒之后」的光标位置，
/// 导致框选结果整体偏移。
/// </summary>
public partial class RoiSelectorWindow : Window
{
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;

    private PixelRect _monitorBounds = PixelRect.Empty;
    private double _scale = 1.0;
    private HwndSource? _source;
    private bool _dragging;
    private int _startX;
    private int _startY;

    public RoiSelectorWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        KeyDown += OnKeyDown;
    }

    /// <summary>确认后的选区（屏幕物理像素）。取消时为 Empty。</summary>
    public PixelRect SelectedRect { get; private set; } = PixelRect.Empty;

    /// <summary>可注入的追踪钩子，用于把框选各步骤写进日志（便于真机排查）。</summary>
    public Action<string>? Trace { get; set; }

    private void Log(string message) => Trace?.Invoke(message);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        (int cursorX, int cursorY) = NativeInput.GetCursorPosition();
        MonitorInfo monitor = MonitorService.FromPoint(cursorX, cursorY);
        _monitorBounds = monitor.Bounds;
        _scale = monitor.Scale <= 0 ? 1.0 : monitor.Scale;

        IntPtr handle = new WindowInteropHelper(this).Handle;

        // 用 SetWindowPos 以物理像素精确覆盖整块显示器（WPF 的 Left/Top 是 DIP，混合 DPI 下易错）。
        NativeInput.SetWindowPos(
            handle,
            NativeInput.HWND_TOPMOST,
            _monitorBounds.X,
            _monitorBounds.Y,
            _monitorBounds.Width,
            _monitorBounds.Height,
            NativeInput.SWP_NOACTIVATE | NativeInput.SWP_SHOWWINDOW);

        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);

        UpdateMask(PixelRect.Empty);
        LayoutChrome();

        Log($"覆盖层就绪：显示器={_monitorBounds} scale={_scale:0.##} hwnd={handle} 客户区={GetClientRectSummary(handle)}");
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        HintText.Text = "拖动框选「神器列表」的整个区域　·　Enter / 右键 = 确认　·　Esc = 取消　·　可重新拖动";
        Activate();
        Focus();
        LayoutChrome();
    }

    /// <summary>客户区左上角相对屏幕的物理偏移，用于把 lParam 客户区坐标换算为屏幕坐标。</summary>
    private static string GetClientRectSummary(IntPtr handle)
    {
        WindowInfo? info = WindowLocator.EnumerateVisibleWindows().FirstOrDefault(w => w.Handle == handle);
        return info is null ? "(未知)" : info.Bounds.ToString();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_LBUTTONDOWN:
                BeginDrag(ToScreen(lParam));
                handled = true;
                break;

            case WM_MOUSEMOVE when _dragging:
                UpdateSelection(ToScreen(lParam));
                handled = true;
                break;

            case WM_LBUTTONUP when _dragging:
                UpdateSelection(ToScreen(lParam));
                _dragging = false;
                Log($"抬起：选区={SelectedRect}");
                handled = true;
                break;

            case WM_RBUTTONUP:
                Log("收到右键，尝试确认。");
                TryConfirm();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>lParam 低 16 位 = 客户区 X，高 16 位 = 客户区 Y（物理像素）。</summary>
    private (int X, int Y) ToScreen(IntPtr lParam)
    {
        long value = lParam.ToInt64();
        int clientX = (short)(value & 0xFFFF);
        int clientY = (short)((value >> 16) & 0xFFFF);

        return (_monitorBounds.X + clientX, _monitorBounds.Y + clientY);
    }

    private void BeginDrag((int X, int Y) point)
    {
        _startX = point.X;
        _startY = point.Y;
        _dragging = true;
        Log($"按下：({point.X},{point.Y})");
        UpdateSelection((point.X, point.Y));
    }

    private void LayoutChrome()
    {
        double width = _monitorBounds.Width / _scale;

        HintPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(HintPanel, Math.Max(0, (width - HintPanel.DesiredSize.Width) / 2));
        Canvas.SetTop(HintPanel, 40);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Log("收到 Esc，取消。");
                SelectedRect = PixelRect.Empty;
                DialogResult = false;
                Close();
                break;

            case Key.Enter:
                Log($"收到 Enter，尝试确认；当前选区={SelectedRect}");
                TryConfirm();
                break;
        }
    }

    private void UpdateSelection((int X, int Y) point)
    {
        var physical = new PixelRect(
            Math.Min(_startX, point.X),
            Math.Min(_startY, point.Y),
            Math.Abs(point.X - _startX),
            Math.Abs(point.Y - _startY));

        SelectedRect = physical;
        UpdateMask(physical);

        if (physical.Width < 2 || physical.Height < 2)
        {
            SelectionOutline.Visibility = Visibility.Collapsed;
            SizePanel.Visibility = Visibility.Collapsed;
            return;
        }

        SelectionOutline.Visibility = Visibility.Visible;
        SizePanel.Visibility = Visibility.Visible;

        double left = (physical.X - _monitorBounds.X) / _scale;
        double top = (physical.Y - _monitorBounds.Y) / _scale;
        double width = physical.Width / _scale;
        double height = physical.Height / _scale;

        Canvas.SetLeft(SelectionOutline, left);
        Canvas.SetTop(SelectionOutline, top);
        SelectionOutline.Width = width;
        SelectionOutline.Height = height;

        SizeText.Text = $"{physical.Width} x {physical.Height} px  @({physical.X},{physical.Y})";

        // 角标贴选区右下，越界时回退到左上内侧。
        SizePanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double badgeW = SizePanel.DesiredSize.Width;
        double badgeH = SizePanel.DesiredSize.Height;
        double badgeLeft = left + width - badgeW;
        double badgeTop = top + height + 6;
        if (badgeTop + badgeH > _monitorBounds.Height / _scale)
        {
            badgeTop = top - badgeH - 6;
        }

        Canvas.SetLeft(SizePanel, Math.Max(0, badgeLeft));
        Canvas.SetTop(SizePanel, Math.Max(0, badgeTop));
    }

    /// <summary>遮罩用 EvenOdd 组合几何在整屏暗色上挖出选区「洞」。</summary>
    private void UpdateMask(PixelRect physical)
    {
        double width = _monitorBounds.Width / _scale;
        double height = _monitorBounds.Height / _scale;

        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(new RectangleGeometry(new Rect(0, 0, width, height)));

        if (physical.Width >= 2 && physical.Height >= 2)
        {
            group.Children.Add(new RectangleGeometry(new Rect(
                (physical.X - _monitorBounds.X) / _scale,
                (physical.Y - _monitorBounds.Y) / _scale,
                physical.Width / _scale,
                physical.Height / _scale)));
        }

        Mask.Data = group;
    }

    private void TryConfirm()
    {
        if (SelectedRect.Width < 8 || SelectedRect.Height < 8)
        {
            Log($"选区过小，拒绝确认：{SelectedRect}");
            HintText.Text = "选区太小，请重新拖动框选整个列表区域（Esc 取消）";
            return;
        }

        Log($"确认选区：{SelectedRect}");
        DialogResult = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _source?.RemoveHook(WndProc);
        base.OnClosed(e);
    }
}
