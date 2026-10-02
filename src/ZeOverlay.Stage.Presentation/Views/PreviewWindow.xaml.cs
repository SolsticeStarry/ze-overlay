using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

using ZeOverlay.Shared;
using ZeOverlay.Infrastructure;

namespace ZeOverlay.Stage.Presentation;

/// <summary>
/// 主界面：左=常驻设置面板，右=预览。把设置从弹窗改为内嵌，去掉「设置」按钮。
/// </summary>
public partial class PreviewWindow : Window
{
    private Action? _onSelectRoi;
    private Action? _onSnapshot;
    private Action? _onOpenShots;
    private Action? _onOpenLogs;
    private Action? _onQuit;
    private bool _settingsCollapsed;

    public PreviewWindow()
    {
        InitializeComponent();

        // 默认停靠屏幕右侧工作区。
        Rect workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left, workArea.Right - Width - 16);
        Top = workArea.Top + 16;
    }

    public bool IsPaused { get; private set; }

    public event EventHandler? PauseChanged;

    /// <summary>内嵌的设置面板（宿主读取其 Configuration / Watchlist）。</summary>
    public SettingsPanel Settings => SettingsPanelControl;

    /// <summary>用户在设置面板点「应用设置」后触发。</summary>
    public event EventHandler? SettingsApplied;

    public void BindActions(
        Action onSelectRoi,
        Action onSnapshot,
        Action onOpenShots,
        Action onOpenLogs,
        Action onQuit)
    {
        _onSelectRoi = onSelectRoi;
        _onSnapshot = onSnapshot;
        _onOpenShots = onOpenShots;
        _onOpenLogs = onOpenLogs;
        _onQuit = onQuit;
    }

    /// <summary>用当前配置初始化左侧设置面板。</summary>
    public void InitializeSettings(AppConfig config, WatchlistConfig watchlist, Func<IReadOnlyList<string>> recentNames)
    {
        SettingsPanelControl.LoadFrom(config, watchlist, recentNames);
        SettingsPanelControl.Applied += (_, _) => SettingsApplied?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>刷新设置面板里的「最近识别」列表。</summary>
    public void RefreshRecentNames(Func<IReadOnlyList<string>> recentNames)
        => SettingsPanelControl.RefreshRecentNames(recentNames);

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

    /// <summary>折叠/展开左侧设置面板，让预览获得更大空间。</summary>
    private void OnToggleSettingsClick(object sender, RoutedEventArgs e)
    {
        _settingsCollapsed = !_settingsCollapsed;
        SettingsColumn.Width = _settingsCollapsed ? new GridLength(0) : new GridLength(620);
        SettingsPanelControl.Visibility = _settingsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        SettingsSplitter.Visibility = _settingsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        SettingsToggleButton.Content = _settingsCollapsed ? "显示设置 ▶" : "◀ 隐藏设置";
    }

    private void OnSelectRoiClick(object sender, RoutedEventArgs e) => _onSelectRoi?.Invoke();
    private void OnSnapshotClick(object sender, RoutedEventArgs e) => _onSnapshot?.Invoke();

    private void OnOpenShotsClick(object sender, RoutedEventArgs e) => _onOpenShots?.Invoke();

    private void OnOpenLogsClick(object sender, RoutedEventArgs e) => _onOpenLogs?.Invoke();

    private void OnQuitClick(object sender, RoutedEventArgs e) => _onQuit?.Invoke();

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
