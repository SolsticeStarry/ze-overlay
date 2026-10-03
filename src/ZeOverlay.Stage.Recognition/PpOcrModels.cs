using System.IO;
using System.Text.RegularExpressions;

namespace ZeOverlay.Stage.Recognition;

/// <summary>
/// PP-OCR rec 模型 / 字典的解析（纯路径逻辑，便于单测）。
///
/// 目的：把「用哪个模型」从代码里挪出来。此前硬编码 v3，实测 v3 对括号数字的读字
/// 既吞位又加位（`[15]`→`门5`、`[52]`→`[521]`）；换 v6 后同一样本 7/7 全对且置信度更高。
/// 现在按 v6 → v4 → v3 顺序自动挑第一个存在的模型，也允许配置显式指定。
/// </summary>
public static class PpOcrModels
{
    /// <summary>自动挑选顺序：越靠前越优先（识别质量）。</summary>
    public static readonly string[] AutoRecModelFiles =
    [
        "ch_PP-OCRv6_rec_infer.onnx",
        "ch_PP-OCRv4_rec_infer.onnx",
        "ch_PP-OCRv3_rec_infer.onnx",
    ];

    /// <summary>兜底字典（v3/v4 共用）。</summary>
    public const string LegacyKeysFile = "ppocr_keys_v1.txt";

    /// <summary>解析结果：模型、字典、给日志/UI 用的引擎标签。</summary>
    public sealed record Resolved(string ModelPath, string KeysPath, string Label);

    /// <summary>
    /// 解析模型与字典路径。
    /// <paramref name="modelFile"/> / <paramref name="keysFile"/> 为空时走自动挑选；
    /// 文件缺失不做抛错（由调用方决定回退），只尽可能给出「最可能有」的路径。
    /// </summary>
    public static Resolved Resolve(string modelsDirectory, string? modelFile = null, string? keysFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);

        string? modelPath = null;

        if (!string.IsNullOrWhiteSpace(modelFile))
        {
            string candidate = Path.IsPathRooted(modelFile)
                ? modelFile
                : Path.Combine(modelsDirectory, modelFile);
            if (File.Exists(candidate))
            {
                modelPath = candidate;
            }
        }

        if (modelPath is null)
        {
            foreach (string name in AutoRecModelFiles)
            {
                string candidate = Path.Combine(modelsDirectory, name);
                if (File.Exists(candidate))
                {
                    modelPath = candidate;
                    break;
                }
            }
        }

        // 一个都不存在时给出兜底路径（调用方 File.Exists 检查后会回退系统 OCR）。
        modelPath ??= Path.Combine(modelsDirectory, AutoRecModelFiles[^1]);

        string dictName = string.IsNullOrWhiteSpace(keysFile) ? DictFileNameFor(modelPath) : keysFile;
        string keysPath = Path.IsPathRooted(dictName) ? dictName : Path.Combine(modelsDirectory, dictName);
        if (!File.Exists(keysPath))
        {
            string legacy = Path.Combine(modelsDirectory, LegacyKeysFile);
            if (File.Exists(legacy))
            {
                keysPath = legacy;
            }
        }

        return new Resolved(modelPath, keysPath, LabelFor(modelPath));
    }

    /// <summary>模型文件名 → 引擎标签，如 <c>ch_PP-OCRv6_rec_infer.onnx</c> → <c>PP-OCRv6-rec</c>。</summary>
    public static string LabelFor(string modelPath)
    {
        Match match = Regex.Match(Path.GetFileNameWithoutExtension(modelPath), @"PP-OCRv(\d+)");
        string version = match.Success ? $"PP-OCRv{match.Groups[1].Value}" : "PP-OCR";
        return $"{version}-rec";
    }

    /// <summary>模型版本对应的字典文件名：v5/v6 有独立字典，其余用 <c>ppocr_keys_v1.txt</c>。</summary>
    public static string DictFileNameFor(string modelPath)
    {
        Match match = Regex.Match(Path.GetFileName(modelPath), @"PP-OCRv(\d+)");
        if (match.Success)
        {
            string version = match.Groups[1].Value;
            if (version is "5" or "6")
            {
                return $"ppocrv{version}_dict.txt";
            }
        }

        return LegacyKeysFile;
    }
}
