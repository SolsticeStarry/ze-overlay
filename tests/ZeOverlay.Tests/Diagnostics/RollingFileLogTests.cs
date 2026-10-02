
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

namespace ZeOverlay.Tests;

public class RollingFileLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ze_overlay_logs_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Info_AppendsStructuredLine()
    {
        using var log = new Log(_dir);

        log.Info("hello 神器");

        string content = File.ReadAllText(log.CurrentPath);
        Assert.Contains("[INFO ]", content);
        Assert.Contains("hello 神器", content);
    }

    [Fact]
    public void Write_ExceedingMaxBytes_RollsAndCapsFileCount()
    {
        // 上限压到最小允许值（64KB），构造超量日志触发轮转。
        using var log = new Log(_dir, "app.log", maxBytes: 64 * 1024, maxFiles: 3);

        for (int i = 0; i < 4000; i++)
        {
            log.Info(new string('x', 100) + i);
        }

        string[] archives = Directory.GetFiles(_dir, "app.*.log");
        string[] all = Directory.GetFiles(_dir, "app*.log");

        Assert.True(archives.Length >= 1, "应至少产生一个轮转归档文件。");
        Assert.True(all.Length <= 3, $"归档数应被限制在 3 以内，实际 {all.Length}。");
    }

    [Fact]
    public void AfterDispose_DoesNotThrow()
    {
        var log = new Log(_dir);
        log.Dispose();

        // 不应抛异常，也不应再写文件。
        log.Info("after dispose");

        Assert.False(File.Exists(log.CurrentPath));
    }
}
