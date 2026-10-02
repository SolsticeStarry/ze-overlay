using System.Text;

namespace ZeOverlay.Core.Tracking;

/// <summary>
/// 名称文本的规范化与相似度。用途是**识别容错**：
/// key 里的玩家名不在关注名单里，OCR 错字无法靠名单纠错，
/// 若不归并就会「同一把神器因为名字读错而每次都被当成新条目」。
/// </summary>
public static class TextSimilarity
{
    /// <summary>去掉空白、全角转半角、统一小写。</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);

        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || c == '\u3000')
            {
                continue;
            }

            // 全角 ASCII（！～）转半角
            char normalized = c is >= '\uFF01' and <= '\uFF5E' ? (char)(c - 0xFEE0) : c;
            builder.Append(char.ToLowerInvariant(normalized));
        }

        return builder.ToString();
    }

    /// <summary>归一化后的相似度（1 = 完全相同，0 = 完全无关）。</summary>
    public static double Ratio(string? left, string? right)
    {
        string a = Normalize(left);
        string b = Normalize(right);

        if (a.Length == 0 && b.Length == 0)
        {
            return 1.0;
        }

        if (a.Length == 0 || b.Length == 0)
        {
            return 0.0;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 1.0;
        }

        int distance = Levenshtein(a, b);
        return 1.0 - distance / (double)Math.Max(a.Length, b.Length);
    }

    private static int Levenshtein(string a, string b)
    {
        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
