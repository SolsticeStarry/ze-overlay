using System.Text.Encodings.Web;
using System.Text.Json;

using ZeOverlay.Shared;

namespace ZeOverlay.Infrastructure;

/// <summary>关注名单（≤10 个）。持久化到 exe 旁的 `watchlist.json`，便于直接手改。</summary>
public sealed class WatchlistConfig
{
    public int Version { get; set; } = 1;

    /// <summary>神器名（名单即筛选；名单外完全隐藏）。</summary>
    public List<string> Names { get; set; } = [];

    /// <summary>显示分组顺序。每个神器名是一类；类内仍按页和行号排序。</summary>
    public List<string> SortOrder { get; set; } = [];

    /// <summary>模糊匹配阈值，越低越宽松（同时用于纠正 OCR 错字）。</summary>
    public double MatchThreshold { get; set; } = 0.85;

    /// <summary>名单上限（PLAN 第 1.2 节：≤10）。</summary>
    public const int MaxNames = 10;
}

public static class WatchlistStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    public static WatchlistConfig Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new WatchlistConfig();
            }

            string json = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(json)
                ? new WatchlistConfig()
                : JsonSerializer.Deserialize<WatchlistConfig>(json, SerializerOptions) ?? new WatchlistConfig();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new WatchlistConfig();
        }
    }

    public static void Save(string path, WatchlistConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, SerializerOptions));
        File.Move(temp, path, overwrite: true);
    }
}

