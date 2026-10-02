using System.Globalization;
using System.Text;
using ZeOverlay.Core.Tracking;

namespace ZeOverlay.Core.Parsing;

/// <summary>一行神器列表解析结果。</summary>
public sealed record ParsedRow(
    string ArtifactName,
    string PlayerName,
    ArtifactState State,
    int? CooldownSeconds,
    int? UsesRemaining,
    int? UsesTotal,
    string RawText);

/// <summary>
/// 行内解析：从 OCR 文本还原出 名称 / 状态 / n/m / 玩家名。
///
/// 模型来自真机实测（`PLAN.md` 第 6.2 节、`docs/REAL_SAMPLES.md`）：
/// 一行是**左对齐的变长文本** `名称 [状态] 可选n/m 玩家名`，
/// 状态标记**不在固定列**，所以不能用固定列切分，必须**括号锚定**。
///
/// 必须容忍的 OCR 现实（全部来自真实 OCR 输出）：
/// - OCR 会在字与字之间插入空格（`范 德 彪`）⇒ 先去掉所有空白；
/// - `]` 常被读成 `I`/`l`/`J`（`[R]` → `[RI`）；
/// - 全角字母数字（`４`）要归一化；
/// - 玩家名里可能出现 `【】`、`〖〗`、`☆`、`．` 等，**不能**把它们当状态括号。
/// </summary>
public static class StatusLineParser
{
    /// <summary>闭合括号的常见误读。仅在后面跟着 CJK 或行尾时才当作括号（避免吃掉以这些字母开头的玩家名）。</summary>
    private const string BracketMisreads = "IlJ1]}）";

    /// <summary>
    /// 开括号的形近字。实测 OCR 会把 `[` 读成 `《`（`[R]` → `《R]》`），
    /// 只认 `[` 会直接解析失败。安全性由「括号内容必须是 `R` 或纯数字」保证：
    /// 玩家名里的 `【猫娘】`、`（一个真正的man）` 内容不满足该条件，会被跳过。
    /// </summary>
    private const string OpeningBrackets = "[［【〖《（(｛{〔";

    /// <summary>闭括号的形近字。</summary>
    private const string ClosingBrackets = "]］】〗》）」)｝}〕";

    public static ParsedRow? Parse(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        string stripped = StripWhitespace(rawText);
        if (stripped.Length == 0)
        {
            return null;
        }

        string normalized = NormalizeFullWidth(stripped);

        bool sawOpeningBracket = false;

        // 从右往左找状态括号：状态在名称之后，取最后一个匹配的 `[` 才能避开名称里可能出现的 `[`。
        for (int i = normalized.Length - 1; i >= 0; i--)
        {
            if (!OpeningBrackets.Contains(normalized[i]))
            {
                continue;
            }

            sawOpeningBracket = true;

            if (!TryParseStatus(normalized, i, out ArtifactState state, out int? cooldown, out int contentEnd))
            {
                continue;
            }

            int cursor = contentEnd;

            // 闭合括号（含常见误读）。OCR 偶尔会吐出重复/变体的闭括号（`[R]】`），一并吃掉。
            if (cursor < normalized.Length && IsClosingBracket(normalized, cursor))
            {
                cursor++;

                while (cursor < normalized.Length && ClosingBrackets.Contains(normalized[cursor]))
                {
                    cursor++;
                }
            }

            // 紧随其后的 n/m
            int? usesRemaining = null;
            int? usesTotal = null;
            int slashEnd = TryParseUses(normalized, cursor, out int remaining, out int total);

            if (slashEnd > cursor)
            {
                usesRemaining = remaining;
                usesTotal = total;
                cursor = slashEnd;
            }

            string name = TrimBracketNoise(stripped[..i]);
            string player = TrimBracketNoise(stripped[cursor..]);

            if (name.Length == 0)
            {
                return null;
            }

            return new ParsedRow(name, player, state, cooldown, usesRemaining, usesTotal, rawText);
        }

        // 兜底：PP-OCR 偶尔会**整个丢掉方括号**（实测 `手电筒[R]烧烤鱼` → `手电筒R烧烤鱼`），
        // 此时上面找不到任何开括号。退化为「裸状态标记」：一段被非字母数字包夹的 `R` 或 1~2 位数字。
        //
        // 只在**完全没有括号**时才启用：解析器一旦发现过括号，就不该再用裸标记去猜，
        // 否则容易从名称/玩家名里抠出个数字当状态。
        if (!sawOpeningBracket
            && TryParseBareStatus(normalized, out int tokenStart, out ArtifactState bareState, out int? bareCooldown, out int tokenEnd))
        {
            string bareName = stripped[..tokenStart].Trim();
            if (bareName.Length > 0)
            {
                int cursor = tokenEnd;
                int? usesRemaining = null;
                int? usesTotal = null;
                int slashEnd = TryParseUses(normalized, cursor, out int remaining, out int total);

                if (slashEnd > cursor)
                {
                    usesRemaining = remaining;
                    usesTotal = total;
                    cursor = slashEnd;
                }

                return new ParsedRow(
                    bareName,
                    stripped[cursor..].Trim(),
                    bareState,
                    bareCooldown,
                    usesRemaining,
                    usesTotal,
                    rawText);
            }
        }

        return null;
    }

    /// <summary>
    /// 找「裸状态标记」：被非字母数字包夹的 `R`，或 1~2 位数字。
    /// 数字限制 1~2 位是为了避开玩家名里的长数字（如 `用户6744311`）；
    /// 且后面不能是 `/`，否则那是 `n/m` 而不是状态。
    /// </summary>
    private static bool TryParseBareStatus(string text, out int tokenStart, out ArtifactState state, out int? cooldown, out int tokenEnd)
    {
        tokenStart = -1;
        tokenEnd = -1;
        state = ArtifactState.Unknown;
        cooldown = null;

        for (int i = 1; i < text.Length; i++)
        {
            if (IsAsciiAlphaNumeric(text[i - 1]))
            {
                continue;
            }

            char c = text[i];

            if (c is 'R' or 'r')
            {
                if (i + 1 < text.Length && IsAsciiAlphaNumeric(text[i + 1]))
                {
                    continue;
                }

                tokenStart = i;
                tokenEnd = i + 1;
                state = ArtifactState.Ready;
                return true;
            }

            if (!char.IsAsciiDigit(c))
            {
                continue;
            }

            int j = i;
            while (j < text.Length && char.IsAsciiDigit(text[j]) && j - i < 3)
            {
                j++;
            }

            int length = j - i;
            if (length is < 1 or > 2)
            {
                i = j - 1;
                continue;
            }

            char following = j < text.Length ? text[j] : '\0';

            // 后面是 `/` ⇒ 这是 `n/m`，把整个 n/m 一起跳过（否则分母会被误当成状态）
            if (following == '/')
            {
                int k = j + 1;
                while (k < text.Length && char.IsAsciiDigit(text[k]))
                {
                    k++;
                }

                i = k - 1;
                continue;
            }

            // 后面是字母数字 ⇒ 属于更长的串
            if (IsAsciiAlphaNumeric(following))
            {
                continue;
            }

            tokenStart = i;
            tokenEnd = j;
            state = ArtifactState.Cooling;
            cooldown = int.Parse(text.AsSpan(i, length), NumberStyles.None, CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    private static bool IsAsciiAlphaNumeric(char c)
        => c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    /// <summary>解析 `[` 之后的状态内容。成功时 <paramref name="contentEnd"/> 指向内容之后。</summary>
    private static bool TryParseStatus(string text, int bracketIndex, out ArtifactState state, out int? cooldown, out int contentEnd)
    {
        state = ArtifactState.Unknown;
        cooldown = null;
        contentEnd = bracketIndex + 1;

        int i = bracketIndex + 1;
        if (i >= text.Length)
        {
            return false;
        }

        char first = text[i];

        // Ready：`R`（大小写都见得到）
        if (first is 'R' or 'r' or 'Ｒ')
        {
            state = ArtifactState.Ready;
            contentEnd = i + 1;
            return true;
        }

        // Cooling：纯数字，最多 3 位（冷却秒数实测为 12/20 这类）
        if (char.IsAsciiDigit(first))
        {
            int start = i;
            while (i < text.Length && char.IsAsciiDigit(text[i]) && i - start < 3)
            {
                i++;
            }

            if (int.TryParse(text.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
            {
                state = ArtifactState.Cooling;
                cooldown = seconds;
                contentEnd = i;
                return true;
            }
        }

        return false;
    }

    private static bool IsClosingBracket(string text, int index)
    {
        char c = text[index];

        if (ClosingBrackets.Contains(c))
        {
            return true;
        }

        if (!BracketMisreads.Contains(c))
        {
            return false;
        }

        // `[R]` 只有 3 个字符，`R` 后面这个字符几乎必然是闭合括号。
        // 实测 `]` 被读成 `I` 非常常见（`[R]` → `[RI`），所以这里倾向于**吃掉**它。
        //
        // 唯一的守门条件：后面若不是小写拉丁字母。
        // - `[RIMr.YYX` → I 后面是 M ⇒ 吃掉 ⇒ 玩家名 = Mr.YYX ✓
        // - `[RI范德彪` → I 后面是 CJK ⇒ 吃掉 ✓
        // - `[R]IamPlayer` → `]` 正常吃掉，I 留给玩家名 ✓
        int next = index + 1;
        if (next >= text.Length)
        {
            return true;
        }

        char following = text[next];
        return !(following is >= 'a' and <= 'z');
    }

    /// <summary>解析 `n/m`；不匹配时返回原位置。</summary>
    private static int TryParseUses(string text, int index, out int remaining, out int total)
    {
        remaining = 0;
        total = 0;

        int i = index;
        var numerator = new StringBuilder();

        while (i < text.Length && char.IsAsciiDigit(text[i]) && numerator.Length < 3)
        {
            numerator.Append(text[i]);
            i++;
        }

        if (numerator.Length == 0 || i >= text.Length || text[i] != '/')
        {
            return index;
        }

        i++;
        var denominator = new StringBuilder();

        while (i < text.Length && char.IsAsciiDigit(text[i]) && denominator.Length < 3)
        {
            denominator.Append(text[i]);
            i++;
        }

        if (denominator.Length == 0)
        {
            return index;
        }

        remaining = int.Parse(numerator.ToString(), CultureInfo.InvariantCulture);
        total = int.Parse(denominator.ToString(), CultureInfo.InvariantCulture);
        return i;
    }

    private static bool IsCjk(char c)
        => c is >= '\u4E00' and <= '\u9FFF' or >= '\u3400' and <= '\u4DBF' or >= '\uF900' and <= '\uFAFF';

    /// <summary>
    /// 去掉名字首尾多出来的括号/竖线噪声。
    /// 实测 PP-OCR 会吐出 `滋水枪@】用户6116041`、`手电筒@|宝宝害怕` 这类读数。
    ///
    /// 只削**首部的闭括号**与**尾部的开括号**（名字不可能这样开头/结尾），
    /// 这样既去掉噪声，又不会伤到合法的全角括号玩家名（如 `【猫娘】繁花凡`）
    /// 或名称里本来就有的 ASCII 方括号。
    /// </summary>
    private static string TrimBracketNoise(string text)
    {
        string trimmed = text.Trim();

        while (trimmed.Length > 0 && (ClosingBrackets.Contains(trimmed[0]) || trimmed[0] is '|' or '｜' or '丨'))
        {
            trimmed = trimmed[1..];
        }

        while (trimmed.Length > 0 && (OpeningBrackets.Contains(trimmed[^1]) || trimmed[^1] is '|' or '｜' or '丨'))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed.Trim();
    }

    private static string StripWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (char c in text)
        {
            if (!char.IsWhiteSpace(c) && c != '\u3000')
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>全角 ASCII → 半角（长度 1:1，下标可直接复用）。`【】〖〗` 不在该区间，保持原样。</summary>
    private static string NormalizeFullWidth(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (char c in text)
        {
            builder.Append(c is >= '\uFF01' and <= '\uFF5E' ? (char)(c - 0xFEE0) : c);
        }

        return builder.ToString();
    }
}
