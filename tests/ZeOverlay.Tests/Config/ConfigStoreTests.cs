
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

public class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ze_overlay_tests_" + Guid.NewGuid().ToString("N"));

    public ConfigStoreTests()
    {
        Directory.CreateDirectory(_dir);
    }

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
    public void Load_MissingFile_ReturnsDefaults()
    {
        AppConfig config = ConfigStore.Load(Path.Combine(_dir, "nope.json"));

        Assert.Equal(1, config.Version);
        Assert.False(config.Roi.IsSet);
        Assert.Equal(2, config.Capture.Fps);
        Assert.Equal(string.Empty, config.Roi.ScreenRect);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        string path = Path.Combine(_dir, "config.json");

        var config = new AppConfig();
        config.Capture.Fps = 5;
        config.Roi.IsSet = true;
        config.Roi.ScreenRect = "100,200,300,400";
        config.Roi.WindowRelative = "0.1,0.2,0.3,0.4";
        config.Roi.TargetWindowProcess = "notepad";
        config.Hotkeys.Snapshot = "Ctrl+Shift+F2";

        ConfigStore.Save(path, config);

        AppConfig loaded = ConfigStore.Load(path);

        Assert.Equal(5, loaded.Capture.Fps);
        Assert.True(loaded.Roi.IsSet);
        Assert.Equal("100,200,300,400", loaded.Roi.ScreenRect);
        Assert.Equal("0.1,0.2,0.3,0.4", loaded.Roi.WindowRelative);
        Assert.Equal("notepad", loaded.Roi.TargetWindowProcess);
        Assert.Equal("Ctrl+Shift+F2", loaded.Hotkeys.Snapshot);
    }

    [Fact]
    public void Save_WritesNonAsciiWithoutEscaping()
    {
        string path = Path.Combine(_dir, "config.json");
        var config = new AppConfig();
        config.Roi.TargetWindowTitle = "反恐精英";

        ConfigStore.Save(path, config);

        // 中文不应被转义成 \uXXXX，便于人工查看与手改。
        Assert.Contains("反恐精英", File.ReadAllText(path));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaultsAndBacksUp()
    {
        string path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, "{ this is not json ");

        AppConfig config = ConfigStore.Load(path);

        Assert.Equal(1, config.Version);
        Assert.False(config.Roi.IsSet);
        Assert.True(File.Exists(Path.Combine(_dir, "config.corrupt.json")));
    }

    [Fact]
    public void Save_IsAtomic_LeavesNoTempFile()
    {
        string path = Path.Combine(_dir, "config.json");
        ConfigStore.Save(path, new AppConfig());

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }
}
