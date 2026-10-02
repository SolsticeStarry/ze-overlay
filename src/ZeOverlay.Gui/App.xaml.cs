using System.Windows;
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

/// <summary>
/// GUI 应用入口（WPF）。启动后进入预览窗口；设置窗口由预览窗口按需打开。
///
/// 离线工具（--analyze-image / --ocr / --recognize / --bench-ocr / --capture-once）
/// 已抽到独立的 <c>ZeOverlay.Cli</c> 控制台项目；本工程只负责 GUI。
/// </summary>
public partial class App : Application
{
    private Host? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 「只做标定」模式：进入框选，确认或取消后立即退出。
        bool selectRoiOnly = e.Args.Any(a => a.Equals("--select-roi", StringComparison.OrdinalIgnoreCase));

        Paths paths = Paths.ForExecutable();
        paths.EnsureDirectories();

        using var log = new Log(paths.LogsDirectory);
        log.Info($"启动 ZeOverlay M0；基目录={paths.BaseDirectory}");

        DispatcherUnhandledException += (_, args) =>
        {
            log.Error("UI 线程未处理异常", args.Exception);
            MessageBox.Show(args.Exception.Message, "ZeOverlay 出错", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                log.Error("后台线程未处理异常", ex);
            }
        };

        _host = new Host(paths, Dispatcher, selectRoiOnly);
        _host.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}
