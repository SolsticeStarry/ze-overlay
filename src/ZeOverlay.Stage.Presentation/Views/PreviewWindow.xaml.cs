using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

using ZeOverlay.Shared;

namespace ZeOverlay.Stage.Presentation;

/// <summary>
/// M0 的验证界面：把抓到的列表区域原样可视化，并显示采集耗时 /
/// ROI / DPI 等「必须真机实测」的信息（PLAN 第 11 节基准在第 15 节确认前不作结论）。
/// </summary>
public partial class PreviewWindow : Window
{
    private Action? _onSelectRoi;
    private Action? _onSnapshot;
    private Action? _onOpenShots;
    private Action? _onOpenLogs;
    private Action? _onQuit;
    private Action? _onSettings;

    public PreviewWindow()
    {
        InitializeComponent();
    }

    public bool IsPaused { get; private set; }

    public event EventHandler? PauseChanged;

    public void BindActions(
        Action onSelectRoi,
        Action onSnapshot,
        Action onOpenShots,
        Action onOpenLogs,
        Action onQuit,
        Action onSettings)
    {
        _onSelectRoi = onSelectRoi;
        _onSnapshot = onSnapshot;
        _onOpenShots = onOpenShots;
        _onOpenLogs = onOpenLogs;
        _onQuit = onQuit;
        _onSettings = onSettings;
    }

    /// <summary>
    /// 显示**实际注册成功**的热键。
    /// 不能写死首选手势：本机 `Ctrl+Alt+R` 被占用，实际生效的是 `Ctrl+Shift+R`，
    /// 提示里写错会让用户按了没反应。
    /// </summary>
    public void SetHotkeyHints(string selectRoi, string snapshot)
    {
        SelectRoiButton.Content = $"重新框选 ({selectRoi})";
        SnapshotButton.Content = $"保存截图 ({snapshot})";
        SelectRoiButton.ToolTip = $"在画面上框选列表区域（{selectRoi}）";
        SnapshotButton.ToolTip = $"保存当前帧（{snapshot}）";

        EmptyHint.Text = $"尚未标定列表区域{Environment.NewLine}按 {selectRoi} 在画面上框选「神器列表」的整个区域";
    }

    public void ShowFrame(BitmapSource? image, string status, string? recognition = null)
    {
        StatusText.Text = status;

        if (recognition is not null)
        {
            RecognitionText.Text = recognition;
        }

        if (image is null)
        {
            PreviewImage.Source = null;
            EmptyHint.Visibility = Visibility.Visible;
            return;
        }

        EmptyHint.Visibility = Visibility.Collapsed;
        PreviewImage.Source = image;
    }

    private void OnSelectRoiClick(object sender, RoutedEventArgs e) => _onSelectRoi?.Invoke();

    private void OnSnapshotClick(object sender, RoutedEventArgs e) => _onSnapshot?.Invoke();

    private void OnOpenShotsClick(object sender, RoutedEventArgs e) => _onOpenShots?.Invoke();

    private void OnOpenLogsClick(object sender, RoutedEventArgs e) => _onOpenLogs?.Invoke();

    private void OnQuitClick(object sender, RoutedEventArgs e) => _onQuit?.Invoke();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => _onSettings?.Invoke();

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        IsPaused = !IsPaused;
        PauseButton.Content = IsPaused ? "继续采集" : "暂停采集";
        PauseChanged?.Invoke(this, EventArgs.Empty);
    }

    public void OpenInExplorer(string path, string fallbackMessage)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                MessageBox.Show(fallbackMessage, "ZeOverlay", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "ZeOverlay", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
