using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using ZeOverlay.Shared;

namespace ZeOverlay.Stage.Parsing;

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
public static class Parser
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

    public static ParsedRow? Parse(
        string? rawText,
        ParserMode mode = ParserMode.Bracket,
        IReadOnlyList<string>? vocabulary = null)
        => mode == ParserMode.Plain ? ParsePlain(rawText, vocabulary) : ParseBracket(rawText);

    /// <summary>
    /// 自动判断行格式：先按本服「状态括号」解析，找不到有效括号再按社区服 Plain 语法。
    /// 两个服的行格式差别是结构性的（有无状态括号），逐行判断即可，**无需人工选档案/语法**。
    /// </summary>
    public static ParsedRow? ParseAuto(string? rawText, IReadOnlyList<string>? vocabulary = null)
    {
        // 关键：这里**不带**裸状态兜底——否则社区服连写行（`人闪1…`）会被本服解析器
        // 把标号数字误当冷却，必须让它落到 Plain 分支。
        ParsedRow? bracket = ParseBracket(rawText, allowBareFallback: false);
        if (bracket is not null)
        {
            return bracket;
        }

        ParsedRow? plain = ParsePlain(rawText, vocabulary);
        if (plain is not null)
        {
            return plain;
        }

        // 最后兜底：本服行**整段丢了方括号**（实机亮底：`袋装火盐门3小小猪头`、`爆闪相机4…`）。
        // 但**仅当这行不以「连写服状态」结尾**时才试——否则会误吃连写行
        // （`优火盐1…就绪` 的 `1` 会被当冷却），也会破坏 Plain 的名表过滤。
        if (HasPlainStatus(rawText))
        {
            return null;
        }

        return ParseBracket(rawText, allowBareFallback: true);
    }

    /// <summary>该行是否以「连写服状态」结尾（`就绪` / `∞`（含被读成单个 0/8）/ `NNs` / `n/m`）。</summary>
    private static bool HasPlainStatus(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return false;
        }

        string text = NormalizeFullWidth(StripWhitespace(rawText));
        TryParsePlainStatus(text, out ArtifactState state, out _, out _, out _, out _);
        return state != ArtifactState.Unknown;
    }

    /// <summary>
    /// 把识别到的名字按**名表**纠错（取最相近的名表项）。用于修 `袋装火盐门`→`袋装火盐`、
    /// `装杏仁水`→`桶装杏仁水` 这类近义读花，让名单匹配/展示用标准写法（否则 0.85 阈值不认、条目像"消失"）。
    /// 名表为空、或不够像（&lt; threshold）、或前两名太接近（差 &lt; margin）时**原样返回**。
    /// </summary>
    public static string CanonicalizeName(
        string name,
        IReadOnlyList<string>? vocabulary,
        double threshold = 0.74,
        double margin = 0.05)
    {
        if (string.IsNullOrWhiteSpace(name) || vocabulary is null || vocabulary.Count == 0)
        {
            return name;
        }

        string best = name;
        double bestScore = 0;
        double second = 0;

        foreach (string entry in vocabulary)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            if (string.Equals(entry, name, StringComparison.Ordinal))
            {
                return entry;
            }

            double score = Similar.Ratio(name, entry);
            if (score > bestScore)
            {
                second = bestScore;
                bestScore = score;
                best = entry;
            }
            else if (score > second)
            {
                second = score;
            }
        }

        return bestScore >= threshold && bestScore - second >= margin ? best : name;
    }

    /// <summary>本服语法：括号锚定。见类注释。</summary>
    private static ParsedRow? ParseBracket(string? rawText, bool allowBareFallback = true)
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
        if (allowBareFallback
            && !sawOpeningBracket
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

    // ---------------- Plain 模式（社区服，无方括号） ----------------

    /// <summary>
    /// 社区服语法：`神器简称+序号 玩家名 状态`，整行连写无分隔符
    /// （见 <c>docs/REAL_SAMPLES.md</c> 样本 #5 / #6）。
    ///
    /// 判定方式（两个真机样本已验证）：
    /// - **状态从右取**：`就绪` / `NNs`（含 `1.5s`，只保留整数部分）/ `n/m` / `∞`；
    /// - **简称从左侧取**：开头的非数字段 = 神器简称，紧随的数字段 = 序号（丢弃）；
    /// - 中间剩下的就是玩家名。
    ///
    /// 现实：PP-OCR 会把 `∞` 读成孤立的 `0`（样本 #5）或 `8`（样本 #6），
    /// 因此把末尾「未被数字或斜杠包围的 1~2 位孤立数字」也当作无限（就绪）处理。
    /// </summary>
    private static ParsedRow? ParsePlain(string? rawText, IReadOnlyList<string>? vocabulary)
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

        string text = NormalizeFullWidth(stripped);

        TryParsePlainStatus(text, out ArtifactState state, out int? cooldown, out int? usesRemaining, out int? usesTotal, out int statusStart);

        // 社区服**每一行都带状态**（就绪 / NNs / n/m / ∞）。没有状态的通常是把列表标题
        // （如「地图神器·人类」）或场景噪声当成了行——这类必须丢弃，否则标题会变成幽灵条目。
        if (state == ArtifactState.Unknown)
        {
            return null;
        }

        string head = statusStart > 0 && statusStart <= text.Length ? text[..statusStart] : text;

        // 从左侧取「简称 + 序号」：第一个数字段之前是简称，数字段是序号（丢弃）。
        int digitStart = -1;
        for (int i = 0; i < head.Length; i++)
        {
            if (char.IsAsciiDigit(head[i]))
            {
                digitStart = i;
                break;
            }
        }

        string name;
        string player;
        int? serverIndex = null;

        if (digitStart > 0 && digitStart < head.Length)
        {
            int digitEnd = digitStart;
            while (digitEnd < head.Length && char.IsAsciiDigit(head[digitEnd]))
            {
                digitEnd++;
            }

            if (int.TryParse(head.AsSpan(digitStart, digitEnd - digitStart), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            {
                serverIndex = index;
            }

            // 标号离谱（OCR 读花，如 `定位310`、`黑闪16`）⇒ 名称/标号都不可信，丢弃。
            // 上限 9：超出基本是把状态数字并进来了，留着它会造出重复条目。
            if (serverIndex is null or < 1 or > 9)
            {
                return null;
            }

            name = head[..digitStart];
            player = head[digitEnd..];
        }
        else if (!TrySplitByVocabulary(head, vocabulary, out name, out player))
        {
            // 没有标号、又切不出已知名称（OCR 把玩家名并了进来）⇒ 名称不可信，丢弃。
            // 若给了名表，则用最长前缀切出名称、其余当玩家名（视频/低清下标号常被读丢）。
            return null;
        }

        name = TrimPlainNoise(name);
        player = TrimPlainNoise(player);

        // 名称必须命中档案名表，否则视为 OCR 读花（`优火盐`/`正位`…）。用它会为同一标号建出重复条目。
        if (name.Length == 0 || (vocabulary is { Count: > 0 } && !MatchesVocabulary(name, vocabulary)))
        {
            return null;
        }

        return new ParsedRow(name, player, state, cooldown, usesRemaining, usesTotal, rawText, ServerIndex: serverIndex);
    }

    private static bool MatchesVocabulary(string name, IReadOnlyList<string> vocabulary)
    {
        foreach (string entry in vocabulary)
        {
            if (Similar.Ratio(name, entry) >= 0.74)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 标号缺失时，用档案名表里**最长的前缀**切出神器名称，其余作为玩家名。
    /// 例：`手电王梦虎`（`手电1` 的 `1` 被读丢）→ 名称 `手电`、玩家 `王梦虎`。
    /// </summary>
    private static bool TrySplitByVocabulary(
        string head,
        IReadOnlyList<string>? vocabulary,
        out string name,
        out string player)
    {
        name = string.Empty;
        player = string.Empty;

        if (vocabulary is null || vocabulary.Count == 0)
        {
            return false;
        }

        string? best = null;

        foreach (string entry in vocabulary)
        {
            if (entry.Length > 0
                && head.StartsWith(entry, StringComparison.Ordinal)
                && (best is null || entry.Length > best.Length))
            {
                best = entry;
            }
        }

        if (best is null)
        {
            return false;
        }

        name = best;
        player = head[best.Length..];
        return true;
    }

    /// <summary>从右侧解析 Plain 模式的状态；未识别到时 <paramref name="tokenStart"/> = 文本长度。</summary>
    private static void TryParsePlainStatus(
        string text,
        out ArtifactState state,
        out int? cooldown,
        out int? usesRemaining,
        out int? usesTotal,
        out int tokenStart)
    {
        state = ArtifactState.Unknown;
        cooldown = null;
        usesRemaining = null;
        usesTotal = null;
        tokenStart = text.Length;

        if (text.Length == 0)
        {
            return;
        }

        // 1) 就绪
        if (text.EndsWith("就绪", StringComparison.Ordinal))
        {
            state = ArtifactState.Ready;
            tokenStart = text.Length - 2;
            return;
        }

        // 2) n/m（剩余/总数）
        Match slash = PlainUsesRegex.Match(text);
        if (slash.Success && slash.Index + slash.Length == text.Length)
        {
            usesRemaining = int.Parse(slash.Groups["r"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
            usesTotal = int.Parse(slash.Groups["t"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
            state = ArtifactState.Ready;
            tokenStart = slash.Index;
            return;
        }

        // 3) 冷却 Ns / N.Ns（丢弃小数，只取整数部分）
        Match cooling = PlainCooldownRegex.Match(text);
        if (cooling.Success && cooling.Index + cooling.Length == text.Length)
        {
            cooldown = int.Parse(cooling.Groups["s"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
            state = ArtifactState.Cooling;
            tokenStart = cooling.Index;
            return;
        }

        // 4) 无限符号
        if (text[^1] == '∞')
        {
            state = ArtifactState.Ready;
            tokenStart = text.Length - 1;
            return;
        }

        // 5) 兜底：∞ 被 OCR 读成孤立数字（0 / 8 / 00…），前后无数字或斜杠。
        Match misread = PlainInfinityMisreadRegex.Match(text);
        if (misread.Success && misread.Index + misread.Length == text.Length)
        {
            state = ArtifactState.Ready;
            tokenStart = misread.Index;
        }
    }

    private static readonly Regex PlainUsesRegex = new(
        @"(?<r>\d{1,3})/(?<t>\d{1,3})$",
        RegexOptions.Compiled);

    /// <summary>冷却：整数可带小数部分，末尾 `s`；小数部分整体丢弃。</summary>
    private static readonly Regex PlainCooldownRegex = new(
        @"(?<s>\d{1,3})(?:\.\d+)?[sS]$",
        RegexOptions.Compiled);

    /// <summary>
    /// `∞` 被 PP-OCR 读成孤立数字时的兜底。**只认实测的单个 `0`/`8`**（见 `REAL_SAMPLES.md` 样本 #5/#6）。
    ///
    /// 早期实现是「任意 1~2 位结尾数字」，但那会把**冷却丢单位**的行误判成就绪——
    /// 连写服冷却显示 `49s`，OCR 偶尔把结尾 `s` 读丢变成 `49`，于是长倒计时会瞬间跳 `[R]`
    /// （实机反馈）。收紧后 `49` 不再被当成 `∞`，该行解析失败被丢弃、由本地外推继续走冷却。
    /// </summary>
    private static readonly Regex PlainInfinityMisreadRegex = new(
        @"(?<![\d/])[08]$",
        RegexOptions.Compiled);

    /// <summary>
    /// 清掉简称/玩家名首尾多出来的符号噪声：OCR 常见的 `|`、`~`、截断省略号，
    /// 以及社区服行首有时会带的 `#`（颜色标记残片）、`@`。
    /// </summary>
    private static string TrimPlainNoise(string text)
    {
        const string noise = "|｜丨~～.·、#@*^";

        string trimmed = text.Trim();

        while (trimmed.Length > 0 && noise.Contains(trimmed[0]))
        {
            trimmed = trimmed[1..];
        }

        while (trimmed.Length > 0 && noise.Contains(trimmed[^1]))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed.Trim();
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
