using System.Text.Encodings.Web;
using System.Text.Json;

using ZeOverlay.Shared;

namespace ZeOverlay.Infrastructure;

/// <summary>config.json 的读写。损坏时备份为 config.corrupt.json 并回退默认值，绝不因配置问题崩溃。</summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    public static AppConfig Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new AppConfig();
            }

            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AppConfig();
            }

            return JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions) ?? new AppConfig();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            TryBackupCorrupt(path);
            return new AppConfig();
        }
    }

    public static void Save(string path, AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string json = JsonSerializer.Serialize(config, SerializerOptions);

        // 原子写入：先写临时文件再替换，避免中途崩溃留下半截配置。
        string temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }

    private static void TryBackupCorrupt(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            string backup = Path.ChangeExtension(path, null) + ".corrupt.json";
            File.Copy(path, backup, overwrite: true);
        }
        catch (IOException)
        {
            // 备份失败不影响主流程。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
