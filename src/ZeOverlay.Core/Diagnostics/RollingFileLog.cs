using System.Globalization;
using System.Text;

namespace ZeOverlay.Core.Diagnostics;

/// <summary>
/// 极简结构化日志：单文件滚动，超过体积上限即轮转，只保留最近 N 个文件
/// （PLAN 第 1.3 节「自动限制数量/体积并滚动清理」）。
/// </summary>
public sealed class RollingFileLog : IDisposable
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _fileName;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private bool _disposed;

    public RollingFileLog(string directory, string fileName = "app.log", long maxBytes = 2 * 1024 * 1024, int maxFiles = 5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        _directory = directory;
        _fileName = fileName;
        _maxBytes = Math.Max(64 * 1024, maxBytes);
        _maxFiles = Math.Max(1, maxFiles);

        Directory.CreateDirectory(_directory);
    }

    public string CurrentPath => Path.Combine(_directory, _fileName);

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    public void Dispose()
    {
        _disposed = true;
    }

    private void Write(string level, string message)
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                RollIfNeeded();

                string line = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] {message}{Environment.NewLine}");

                File.AppendAllText(CurrentPath, line, Encoding.UTF8);
            }
            catch (IOException)
            {
                // 日志写失败不能影响主流程。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void RollIfNeeded()
    {
        string current = CurrentPath;
        if (!File.Exists(current) || new FileInfo(current).Length < _maxBytes)
        {
            return;
        }

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string archived = Path.Combine(_directory, $"{Path.GetFileNameWithoutExtension(_fileName)}.{stamp}.log");

        try
        {
            File.Move(current, archived, overwrite: true);
        }
        catch (IOException)
        {
            return;
        }

        TrimOldFiles();
    }

    private void TrimOldFiles()
    {
        string pattern = $"{Path.GetFileNameWithoutExtension(_fileName)}.*.log";
        string[] files = Directory.GetFiles(_directory, pattern);

        if (files.Length <= _maxFiles - 1)
        {
            return;
        }

        foreach (string file in files
                     .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                     .Skip(_maxFiles - 1))
        {
            try
            {
                File.Delete(file);
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
