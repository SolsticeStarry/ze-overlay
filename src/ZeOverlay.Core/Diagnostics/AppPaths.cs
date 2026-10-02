namespace ZeOverlay.Core.Diagnostics;

/// <summary>
/// PLAN 第 1.3 节：配置 / 日志 / 截图 / 名单固定在 exe 旁。
/// 开发期（dotnet run）落在 bin 目录旁，可接受。
/// </summary>
public sealed class AppPaths
{
    private AppPaths(string baseDirectory)
    {
        BaseDirectory = baseDirectory;
    }

    public string BaseDirectory { get; }

    public string ConfigFile => Path.Combine(BaseDirectory, "config.json");

    public string WatchlistFile => Path.Combine(BaseDirectory, "watchlist.json");

    public string ShotsDirectory => Path.Combine(BaseDirectory, ShotsDirName);

    public string LogsDirectory => Path.Combine(BaseDirectory, LogsDirName);

    public string ShotsDirName { get; set; } = "shots";

    public string LogsDirName { get; set; } = "logs";

    public static AppPaths ForExecutable()
    {
        string baseDir = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(baseDir))
        {
            baseDir = Directory.GetCurrentDirectory();
        }

        return new AppPaths(baseDir);
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(BaseDirectory);
        Directory.CreateDirectory(ShotsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
