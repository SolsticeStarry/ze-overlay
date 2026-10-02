using System.Globalization;

using ZeOverlay.Shared;
using ZeOverlay.Win32;

namespace ZeOverlay.Stage.ScreenCapture;

/// <summary>
/// 截图留档：按时间戳命名，超过上限自动删除最旧文件
/// （PLAN 第 1.3 节「自动限制数量/体积并滚动清理」）。
/// </summary>
public sealed class Shots
{
    private readonly string _directory;
    private readonly int _maxShots;

    public Shots(string directory, int maxShots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _directory = directory;
        _maxShots = Math.Max(1, maxShots);
        System.IO.Directory.CreateDirectory(_directory);
    }

    public string Directory => _directory;

    public int Count => System.IO.Directory.Exists(_directory)
        ? System.IO.Directory.GetFiles(_directory, "*.png").Length
        : 0;

    /// <summary>保存一张截图，返回落盘路径。</summary>
    public string Save(ImageFrame frame, string prefix = "roi")
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        string name = string.Create(
            CultureInfo.InvariantCulture,
            $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");

        string path = Path.Combine(_directory, name);
        BitmapCodec.SavePng(frame, path);
        Trim();
        return path;
    }

    /// <summary>删除超出数量的最旧截图。</summary>
    public void Trim()
    {
        string[] files;
        try
        {
            files = System.IO.Directory.GetFiles(_directory, "*.png");
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        if (files.Length <= _maxShots)
        {
            return;
        }

        foreach (string file in files
                     .OrderByDescending(f => System.IO.File.GetCreationTimeUtc(f))
                     .Skip(_maxShots))
        {
            try
            {
                System.IO.File.Delete(file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
