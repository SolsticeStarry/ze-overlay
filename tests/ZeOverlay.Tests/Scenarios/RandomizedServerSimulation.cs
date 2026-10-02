using ZeOverlay.Shared;

namespace ZeOverlay.Tests.Scenarios;

/// <summary>仿真帧里的一行（既用于拼原始 OCR 文本，也用于可视化分列渲染）。</summary>
public sealed record SimulatedHudRow(
    string Name,
    int? Index,
    string Player,
    ArtifactState State,
    int? CooldownSeconds,
    int? UsesRemaining,
    int? UsesTotal,
    string StatusText,
    string Raw);

/// <summary>仿真过程中发生的一次随机事件。</summary>
public sealed record SoakEvent(double At, string Kind, string Text);

/// <summary>当前"场景"背景色（随机漂移，模拟地图/光照不断变化）。</summary>
public sealed record SceneState(double Hue, double Sat, double Light);

/// <summary>某一秒的完整快照：喂进去的行 + 跟踪/展示结果 + 这一刻发生的事件 + 当时的场景背景色。</summary>
public sealed record SoakFrame(
    double At,
    IReadOnlyList<SimulatedHudRow> Rows,
    IReadOnlyList<TrackerEntryView> Overlay,
    IReadOnlyList<SoakEvent> Events,
    SceneState Scene);

public sealed record SoakRun(
    ServerKind Kind,
    int Seed,
    int Seconds,
    IReadOnlyList<SoakFrame> Frames,
    IReadOnlyList<string> Violations,
    int PickUps,
    int Losses,
    int Burns,
    int Reorders,
    int PlayerChanges,
    int Misses,
    int SameNamePickUps,
    int MaxSameName,
    double AverageCount,
    int MinCount,
    int MaxCount,
    double AverageArtifacts,
    int MinArtifacts,
    int MaxArtifacts);

/// <summary>浸泡仿真的可调参数（模糊测试会随机化这些值）。</summary>
public sealed record SoakOptions
{
    /// <summary>列表规模目标；&lt;=0 用按服默认（翻页 EXG 铺满第一页、FYS 10 个上下）。</summary>
    public int TargetCount { get; init; }

    /// <summary>单页行数上限；&lt;=0 用按服默认（EXG 12 / FYS 16）。</summary>
    public int MaxRows { get; init; }

    /// <summary>是否模拟翻页；null = 按服默认（EXG 是 / FYS 否）。</summary>
    public bool? Paging { get; init; }

    /// <summary>逻辑神器总数上限（翻页时可以 &gt; 单页行数）。&lt;=0 用按服默认。</summary>
    public int MaxArtifacts { get; init; }

    /// <summary>翻页间隔（秒）：EXG 大约每这么久在第 1/2 页之间翻一次。</summary>
    public double PageFlipSeconds { get; init; } = 5.0;

    /// <summary>FYS 标号取值范围 1..MaxIndex。</summary>
    public int MaxIndex { get; init; } = 3;

    /// <summary>整帧列表消失（未打开/被遮挡）的概率。</summary>
    public double EmptyFrameBase { get; init; }

    public double PickupBase { get; init; } = 0.10;

    public double LossBase { get; init; } = 0.07;

    public double BurnBase { get; init; } = 0.12;

    public double PlayerChangeBase { get; init; } = 0.08;

    public double ReorderBase { get; init; } = 0.10;

    public double MissBase { get; init; } = 0.05;

    /// <summary>FYS 标号被 OCR 整段读丢的概率（触发「同名最近槽位借标号」）。</summary>
    public double LabelLossBase { get; init; }

    /// <summary>行消失超时（秒）。</summary>
    public double DisappearSeconds { get; init; } = 6.0;

    /// <summary>帧间隔基准（秒）。</summary>
    public double TickBaseSeconds { get; init; } = 1.0;

    /// <summary>帧间隔抖动比例（0~1）：实际间隔 = 基准 × (1 ± 抖动)。用于打时间桶/超时/外推边界。</summary>
    public double TickJitter { get; init; }
}

/// <summary>
/// 连续随机仿真：在一段时间内，以随机时刻混入「拾取新增 / 使用冷却 / 丢失 / 回流换行 / 换玩家 / 倒计时 / OCR 漏读」，
/// 每秒生成一帧假 HUD 文本喂进真实管线，并逐帧校验不变量。
///
/// 随机源固定种子 ⇒ 结果可复现；<see cref="SoakRun.Violations"/> 为空即代表这段随机流没有把跟踪器带崩。
///
/// 不变量：
/// A. 本帧识别到的每个身份都必须出现在快照里（不漏）；
/// B. 快照里任何条目的 LastSeen 距今不超过消失超时 + 1.5s（不长生）；
/// C. 名单为空时，S6 展示条目的身份集合 == S5 快照（全链路一致）。
/// </summary>
public static class RandomizedServerSimulation
{
    private static readonly string[] ExgNames =
    [
        "皇家口粮", "紫色瓶中闪电", "爆闪相机", "滋水枪", "黑色瓶中闪电",
        "手电筒", "灵体定位仪", "袋装火盐", "桶装杏仁水", "荧光棒",
    ];

    private static readonly string[] ExgPlayers =
    [
        "亦陌雕", "用户6744311", "一个真正的man", "Mr.YYYX", "真就一颗a", "不会狙娱乐",
        "游戏尘寰丶", "小柒Lucky", "街望", "豆浆机洗脚", "楚门潇", "范德彪の奇妙冒险", "宝宝害怕", "烧烤鱼",
    ];

    private static readonly string[] FysNames =
    [
        "人闪", "口粮", "大火盐", "大荧光", "定位", "手电", "桶装水", "水枪", "黑闪", "爆闪",
    ];

    private static readonly string[] FysPlayers =
    [
        "绿光投影仪", "菠萝霸霸", "纯爱薄纱牛头", "Evi", "米浴浴OVO", "漆黑蝶默罗兰", "大玖的蓝色",
        "呆头呆脑很迷糊", "小细节大进步", "高大英俊的老六", "霓光Niko", "屠夫优势图", "老默",
        "大头吧唧", "士享爻", "雨露之冥", "知意雨", "请叫我熊", "宇宙机器人", "苦涩的柠檬",
        "无名字的好人", "花昙の魔女",
    ];

    public static SoakRun Run(ServerKind kind, int seed = 20261003, int seconds = 120, SoakOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);
        options ??= new SoakOptions();

        var sim = new ServerSimulator(kind, disappearAfterSeconds: options.DisappearSeconds);
        var rng = new Random(seed);

        bool paging = options.Paging ?? (kind == ServerKind.Exg);
        int pageSize = options.MaxRows > 0 ? options.MaxRows : (kind == ServerKind.Fys ? 16 : 12);
        int maxIndex = Math.Clamp(options.MaxIndex, 1, 9);
        // 翻页时逻辑总数可超过单页（第二页行数明显更少，低于翻页判据阈值 60%）。
        int capacity = options.MaxArtifacts > 0 ? options.MaxArtifacts : (paging ? pageSize + 7 : pageSize);
        // FYS 目标 12（约 12 个）；EXG 13（第一页 12 + 第二页 1，便于每 ~5s 翻页）。
        int defaultTarget = paging ? 13 : 12;
        int target = Math.Clamp(options.TargetCount > 0 ? options.TargetCount : defaultTarget, 2, capacity);
        int maxRows = capacity;

        var arts = new List<Art>();
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);

        // 开局刻意放一组「同类多神器」：
        //   FYS = 水枪1 / 水枪2 / 水枪3（同名不同标号）；
        //   EXG = 同一把神器 ×2（不同玩家，真机样本里 滋水枪 ×2、爆闪相机 ×2）。
        if (kind == ServerKind.Fys)
        {
            string family = FysNames[rng.Next(FysNames.Length)];
            for (int idx = 1; idx <= Math.Min(3, maxIndex); idx++)
            {
                AddSpecific(kind, rng, arts, usedKeys, family, idx);
            }
        }
        else
        {
            string dup = ExgNames[rng.Next(ExgNames.Length)];
            AddSpecific(kind, rng, arts, usedKeys, dup, null);
            AddSpecific(kind, rng, arts, usedKeys, dup, null);
        }

        // 拉到目标规模（平均 10 个上下）。
        while (arts.Count < target)
        {
            if (AddNew(kind, rng, arts, usedKeys, maxIndex) is null)
            {
                break; // FYS 的「名称+标号」身份池已满
            }
        }

        // 开局给一个长冷却，让倒计时一开始就存在。
        arts[^1].State = ArtifactState.Cooling;
        arts[^1].Cooldown = rng.Next(15, 45);

        var frames = new List<SoakFrame>();
        var logicalCounts = new List<int>();
        var violations = new List<string>();
        int pickUps = 0, losses = 0, burns = 0, reorders = 0, playerChanges = 0, misses = 0, sameNamePickUps = 0;

        // 场景背景色：绕各自基准色做随机游走，偶发一次大跳（切区/天气变化）。
        double hue = kind == ServerKind.Fys ? 32 : 78;
        double sat = kind == ServerKind.Fys ? 30 : 34;
        double light = kind == ServerKind.Fys ? 16 : 20;

        int visiblePage = 1;
        double nextFlipAt = options.PageFlipSeconds;

        double now = 0;
        double dt = 0; // 距上一帧的真实间隔（首帧为 0）

        while (now <= seconds + 1e-9)
        {
            var events = new List<SoakEvent>();

            // 0) 冷却按**真实经过的时间**下降（帧间隔抖动时不能按帧数减）。
            foreach (Art a in arts)
            {
                if (a.State == ArtifactState.Cooling && (a.Cooldown -= dt) <= 0)
                {
                    a.State = ArtifactState.Ready;
                    a.Cooldown = 0;
                }
            }

            // 1) 场景背景色加速随机漂移（大扰动，颜色每帧都在明显变）。
            hue = Wrap(hue + (rng.NextDouble() - 0.5) * 70, 360);
            if (rng.NextDouble() < 0.12)
            {
                // 频繁大跳：切区 / 天气 / 昼夜变化。
                hue = Wrap(hue + (rng.NextDouble() - 0.5) * 360, 360);
            }

            sat = Math.Clamp(sat + (rng.NextDouble() - 0.5) * 30, 20, 98);
            light = Math.Clamp(light + (rng.NextDouble() - 0.5) * 22, 10, 46);
            var scene = new SceneState(Math.Round(hue, 1), Math.Round(sat, 1), Math.Round(light, 1));
            // 2) 随机事件（随机时刻 = 下一秒以一定概率发生）。增减围绕 target 波动。
            double pickupChance = options.PickupBase + Math.Clamp((target - arts.Count) * 0.12, 0, 0.5);
            if (rng.NextDouble() < pickupChance && arts.Count < maxRows)
            {
                Art? added = AddNew(kind, rng, arts, usedKeys, maxIndex);
                if (added is not null)
                {
                    pickUps++;
                    bool sameName = arts.Count(a => a.Name == added.Name) > 1;
                    if (sameName)
                    {
                        sameNamePickUps++;
                    }

                    events.Add(new SoakEvent(now, "拾取", $"捡起了 {Label(added)}（{added.Player}）{(sameName ? " · 同类" : string.Empty)}"));
                }
            }

            if (rng.NextDouble() < options.BurnBase)
            {
                Art? burnTarget = Pick(arts, a => a.State == ArtifactState.Ready);
                if (burnTarget is not null)
                {
                    burns++;
                    if (rng.NextDouble() < 0.5)
                    {
                        burnTarget.State = ArtifactState.Cooling;
                        burnTarget.Cooldown = rng.Next(5, 46);
                        events.Add(new SoakEvent(now, "使用", $"{Label(burnTarget)} 进入冷却 {burnTarget.Cooldown}s"));
                    }
                    else if (burnTarget.UsesTotal is { } total && burnTarget.UsesRemaining is { } left && left > 1)
                    {
                        burnTarget.UsesRemaining = left - 1;
                        events.Add(new SoakEvent(now, "使用", $"{Label(burnTarget)} 剩余 {burnTarget.UsesRemaining}/{total}"));
                    }
                    else
                    {
                        burnTarget.State = ArtifactState.Cooling;
                        burnTarget.Cooldown = rng.Next(8, 30);
                        events.Add(new SoakEvent(now, "使用", $"{Label(burnTarget)} 用掉，冷却 {burnTarget.Cooldown}s"));
                    }
                }
            }

            double lossChance = options.LossBase + Math.Clamp((arts.Count - target) * 0.12, 0, 0.5);
            if (rng.NextDouble() < lossChance && arts.Count > 3)
            {
                Art lost = arts[rng.Next(arts.Count)];
                arts.Remove(lost);
                usedKeys.Remove(KeyOf(lost));
                losses++;
                events.Add(new SoakEvent(now, "丢失", $"{Label(lost)} 从列表消失"));
            }

            if (rng.NextDouble() < options.PlayerChangeBase && arts.Count > 0)
            {
                Art changed = arts[rng.Next(arts.Count)];
                string old = changed.Player;
                changed.Player = PickPlayer(kind, rng, changed.Player);
                playerChanges++;
                events.Add(new SoakEvent(now, "换人", $"{Label(changed)}：{old} → {changed.Player}"));
            }

            if (rng.NextDouble() < options.ReorderBase && arts.Count > 1)
            {
                Shuffle(arts, rng);
                reorders++;
                events.Add(new SoakEvent(now, "换行", "列表整体顺序变化（新增/删除导致的位移）"));
            }

            // 3) 决定这一帧显示哪一页：翻页服可在第 1/2 页之间切换（模拟玩家翻页）。
            int previousPage = visiblePage;
            if (paging)
            {
                if (arts.Count <= pageSize)
                {
                    visiblePage = 1;
                    nextFlipAt = now + options.PageFlipSeconds;
                }
                else if (now >= nextFlipAt)
                {
                    // 约每 PageFlipSeconds 秒翻一次（±30% 抖动）。
                    visiblePage = visiblePage == 1 ? 2 : 1;
                    nextFlipAt = now + (options.PageFlipSeconds * (0.7 + (rng.NextDouble() * 0.6)));
                }
            }
            else
            {
                visiblePage = 1;
            }

            if (visiblePage != previousPage)
            {
                events.Add(new SoakEvent(now, "翻页", $"切到第 {visiblePage} 页"));
            }

            // 偶尔整帧列表消失（玩家没开列表/被遮挡）：这一帧没有行。
            bool emptyFrame = options.EmptyFrameBase > 0 && rng.NextDouble() < options.EmptyFrameBase;
            IReadOnlyList<Art> visible;
            if (emptyFrame)
            {
                visible = [];
                visiblePage = 1;
                nextFlipAt = now + options.PageFlipSeconds;
                events.Add(new SoakEvent(now, "隐去", "列表未显示（整帧无行）"));
            }
            else
            {
                visible = visiblePage == 2
                    ? arts.Skip(pageSize).Take(pageSize).ToList()
                    : arts.Take(pageSize).ToList();
            }

            // 4) 生成这一帧的 HUD 行；偶尔漏读一行（OCR 现实），FYS 还可能把标号整段读丢。
            bool miss = visible.Count > 1 && rng.NextDouble() < options.MissBase;
            int missIndex = miss ? rng.Next(visible.Count) : -1;
            var rows = new List<SimulatedHudRow>(visible.Count);
            for (int i = 0; i < visible.Count; i++)
            {
                if (i == missIndex)
                {
                    misses++;
                    events.Add(new SoakEvent(now, "漏读", $"{Label(visible[i])} 这一帧 OCR 没读到"));
                    continue;
                }

                bool loseLabel = kind == ServerKind.Fys
                    && visible[i].Index is not null
                    && options.LabelLossBase > 0
                    && rng.NextDouble() < options.LabelLossBase;
                if (loseLabel)
                {
                    events.Add(new SoakEvent(now, "漏读", $"{Label(visible[i])} 的标号被读丢"));
                }

                rows.Add(Render(kind, visible[i], omitIndex: loseLabel));
            }

            // 4) 喂真实管线（时间用 now，可为非整秒）。
            string[] raw = rows.Select(r => r.Raw).ToArray();
            sim.Feed(now, raw);
            IReadOnlyList<TrackerEntryView> snapshot = sim.Snapshot();
            IReadOnlyList<DisplayEntry> display = sim.LastDisplay;

            // 5) 校验不变量。
            CheckFrame(kind, now, sim.Now, options.DisappearSeconds, rows, snapshot, display, violations);

            frames.Add(new SoakFrame(now, rows, snapshot, events, scene));
            logicalCounts.Add(arts.Count);

            // 下一帧的间隔：基准 × 抖动（非整秒）。
            dt = NextTick(rng, options);
            now += dt;
        }

        int maxSameName = frames.Count == 0
            ? 0
            : frames.Max(f => f.Rows.Count == 0 ? 0 : f.Rows.GroupBy(r => r.Name).Max(g => g.Count()));

        double averageCount = frames.Count == 0 ? 0 : Math.Round(frames.Average(f => (double)f.Rows.Count), 2);
        int minCount = frames.Count == 0 ? 0 : frames.Min(f => f.Rows.Count);
        int maxCount = frames.Count == 0 ? 0 : frames.Max(f => f.Rows.Count);

        double averageArtifacts = logicalCounts.Count == 0 ? 0 : Math.Round(logicalCounts.Average(), 2);
        int minArtifacts = logicalCounts.Count == 0 ? 0 : logicalCounts.Min();
        int maxArtifacts = logicalCounts.Count == 0 ? 0 : logicalCounts.Max();

        return new SoakRun(
            kind, seed, seconds, frames, violations,
            pickUps, losses, burns, reorders, playerChanges, misses, sameNamePickUps, maxSameName,
            averageCount, minCount, maxCount,
            averageArtifacts, minArtifacts, maxArtifacts);
    }

    // ---------------------------------------------------------------- 不变量

    private static void CheckFrame(
        ServerKind kind,
        double t,
        DateTimeOffset now,
        double disappearSeconds,
        IReadOnlyList<SimulatedHudRow> rows,
        IReadOnlyList<TrackerEntryView> snapshot,
        IReadOnlyList<DisplayEntry> display,
        List<string> violations)
    {
        // A. 本帧识别到的每个身份必须都在快照里。FYS 标号读丢（null）时不再作硬性要求
        //    （跟踪器为避免污染真实兄弟可能安全跳过该行）。
        foreach (SimulatedHudRow row in rows)
        {
            if (kind == ServerKind.Fys && row.Index is null)
            {
                continue;
            }

            bool present = kind == ServerKind.Fys
                ? snapshot.Any(e => e.ArtifactName == row.Name && e.ServerIndex == row.Index)
                : snapshot.Any(e => e.ArtifactName == row.Name);

            if (!present)
            {
                violations.Add(
                    $"t={t:0.#}s：本帧识别到「{row.Name}{(row.Index is { } i ? "·" + i : string.Empty)}」但快照里没有"
                    + $"；帧=[{string.Join(", ", rows.Select(r => r.Name + (r.Index is { } x ? "·" + x : string.Empty)))}]"
                    + $" 快照=[{string.Join(", ", snapshot.Select(e => e.ArtifactName + (e.ServerIndex is { } y ? "·" + y : string.Empty)))}]");
            }
        }

        // D. 刚读到的行，其内容（状态/冷却/uses）应与快照一致。
        //    只校验 FYS：EXG 是行槽位身份，同名多把在新增/删除/换行位移后无法区分是哪一把，
        //    内容允许短暂沿用旧值（DESIGN §6 已知限制），由确定性场景用例单独覆盖。
        if (kind == ServerKind.Fys)
        {
            foreach (SimulatedHudRow row in rows)
            {
                if (row.Index is not { } index)
                {
                    continue; // 标号读丢，无法稳定对应
                }

                TrackerEntryView? entry = snapshot.FirstOrDefault(
                    e => e.ArtifactName == row.Name && e.ServerIndex == index);
                if (entry is null)
                {
                    continue; // 身份缺失已由 A 报警
                }

                if (entry.State != row.State)
                {
                    violations.Add($"t={t:0.#}s：「{row.Name}·{index}」状态不符，期望 {row.State} 实际 {entry.State}");
                }

                if (row.State == ArtifactState.Cooling && entry.CooldownSeconds != row.CooldownSeconds)
                {
                    violations.Add($"t={t:0.#}s：「{row.Name}·{index}」冷却不符，期望 {row.CooldownSeconds} 实际 {entry.CooldownSeconds}（源={entry.Source} miss={entry.MissedSessions}）");
                }

                if (row.State == ArtifactState.Ready && entry.CooldownSeconds is not null)
                {
                    violations.Add($"t={t:0.#}s：「{row.Name}·{index}」已就绪但仍有余温 {entry.CooldownSeconds}");
                }

                // uses 只在画面确实显示了 `n/m` 时校验（冷却行显示的是秒数，不含 n/m）。
                if (row.StatusText.Contains('/')
                    && (entry.UsesRemaining != row.UsesRemaining || entry.UsesTotal != row.UsesTotal))
                {
                    violations.Add($"t={t:0.#}s：「{row.Name}·{index}」uses 不符，期望 {row.UsesRemaining}/{row.UsesTotal} 实际 {entry.UsesRemaining}/{entry.UsesTotal}");
                }
            }
        }

        // B. 任何条目都不应超过消失超时太久还留着。
        foreach (TrackerEntryView e in snapshot)
        {
            double age = (now - e.LastSeen).TotalSeconds;
            if (age > disappearSeconds + 1.5)
            {
                violations.Add($"t={t:0.#}s：「{e.ArtifactName}」已 {age:0.#}s 未更新仍在快照里");
            }
        }

        // C. S6（名单为空）与 S5 身份集合一致。
        HashSet<string> snapKeys = snapshot.Select(IdentityOf).ToHashSet(StringComparer.Ordinal);
        HashSet<string> dispKeys = display.Select(d => IdentityOf(d.Name, d.ServerIndex)).ToHashSet(StringComparer.Ordinal);
        if (!snapKeys.SetEquals(dispKeys))
        {
            violations.Add($"t={t:0.#}s：S5 与 S6 条目不一致（快照 {snapKeys.Count} / 展示 {dispKeys.Count}）");
        }
    }

    private static string IdentityOf(TrackerEntryView e)
        => e.ServerIndex is { } i ? $"{e.ArtifactName}·{i}" : e.ArtifactName;

    private static string IdentityOf(string name, int? index)
        => index is { } i ? $"{name}·{i}" : name;

    // ---------------------------------------------------------------- 生成/渲染

    private static Art? AddNew(ServerKind kind, Random rng, List<Art> arts, HashSet<string> usedKeys, int maxIndex = 3)
    {
        // EXG：同名神器会为不同玩家重复出现（真机：滋水枪 ×2、爆闪相机 ×2），不按名称去重。
        if (kind == ServerKind.Exg)
        {
            return AddSpecific(kind, rng, arts, usedKeys, ExgNames[rng.Next(ExgNames.Length)], null);
        }

        // FYS：身份 = 名称 + 标号；组合池满时不再新增（否则会造出重复身份）。
        if (usedKeys.Count >= FysNames.Length * maxIndex)
        {
            return null;
        }

        // 约一半的情况去凑「同类多神器」——给已有名称补一个没出现过的标号（水枪1/2/3）。
        if (rng.NextDouble() < 0.55)
        {
            var families = FysNames
                .Select(name => (name, used: arts.Where(a => a.Name == name).Select(a => a.Index ?? 0).ToHashSet()))
                .Where(x => Enumerable.Range(1, maxIndex).Any(i => !x.used.Contains(i)))
                .ToList();

            if (families.Count > 0)
            {
                (string name, HashSet<int> used) chosen = families[rng.Next(families.Count)];
                int free = Enumerable.Range(1, maxIndex).First(i => !chosen.used.Contains(i));
                return AddSpecific(kind, rng, arts, usedKeys, chosen.name, free);
            }
        }

        for (int attempt = 0; attempt < 50; attempt++)
        {
            string name = FysNames[rng.Next(FysNames.Length)];
            int index = rng.Next(1, maxIndex + 1);
            if (usedKeys.Contains($"{name}·{index}"))
            {
                continue;
            }

            return AddSpecific(kind, rng, arts, usedKeys, name, index);
        }

        return null;
    }

    private static Art AddSpecific(
        ServerKind kind,
        Random rng,
        List<Art> arts,
        HashSet<string> usedKeys,
        string name,
        int? index)
    {
        if (kind == ServerKind.Fys && index is { } i)
        {
            usedKeys.Add($"{name}·{i}");
        }

        bool hasUses = rng.NextDouble() < 0.3;
        int total = hasUses ? rng.Next(2, 7) : 0;

        var art = new Art
        {
            Name = name,
            Index = index,
            Player = kind == ServerKind.Fys ? FysPlayers[rng.Next(FysPlayers.Length)] : ExgPlayers[rng.Next(ExgPlayers.Length)],
            State = ArtifactState.Ready,
            UsesRemaining = hasUses ? total : null,
            UsesTotal = hasUses ? total : null,
            // FYS 就绪有时显示 ∞（真机里 OCR 常把它读成 0/8）。
            Infinite = kind == ServerKind.Fys && rng.NextDouble() < 0.35,
        };

        arts.Add(art);
        return art;
    }

    private static SimulatedHudRow Render(ServerKind kind, Art a, bool omitIndex = false)
    {
        string raw;
        string statusText;
        int? readIndex = a.Index;

        if (kind == ServerKind.Fys)
        {
            if (omitIndex && a.Index is not null)
            {
                readIndex = null; // 标号被 OCR 整段读丢
            }

            statusText = a.State == ArtifactState.Cooling
                ? $"{(int)Math.Ceiling(a.Cooldown)}s"
                : a is { UsesRemaining: { } r, UsesTotal: { } t } ? $"{r}/{t}"
                : a.Infinite ? "∞" : "就绪";
            raw = $"{a.Name}{(readIndex is { } idx ? idx.ToString() : string.Empty)}{a.Player}{statusText}";
        }
        else
        {
            statusText = a.State == ArtifactState.Cooling ? ((int)Math.Ceiling(a.Cooldown)).ToString() : "R";
            raw = a is { UsesRemaining: { } r, UsesTotal: { } t }
                ? $"{a.Name} [{statusText}]{r}/{t} {a.Player}"
                : $"{a.Name} [{statusText}] {a.Player}";
        }

        return new SimulatedHudRow(
            a.Name, readIndex, a.Player, a.State,
            a.State == ArtifactState.Cooling ? (int?)Math.Ceiling(a.Cooldown) : null,
            a.UsesRemaining, a.UsesTotal, statusText, raw);
    }

    private static Art? Pick(List<Art> arts, Func<Art, bool> predicate)
    {
        var candidates = arts.Where(predicate).ToList();
        return candidates.Count == 0 ? null : candidates[0];
    }

    private static string PickPlayer(ServerKind kind, Random rng, string current)
    {
        string[] pool = kind == ServerKind.Fys ? FysPlayers : ExgPlayers;
        for (int i = 0; i < 10; i++)
        {
            string candidate = pool[rng.Next(pool.Length)];
            if (candidate != current)
            {
                return candidate;
            }
        }

        return current;
    }

    private static void Shuffle(List<Art> arts, Random rng)
    {
        for (int i = arts.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (arts[i], arts[j]) = (arts[j], arts[i]);
        }
    }

    private static string Label(Art a) => a.Index is { } i ? $"{a.Name}·{i}" : a.Name;

    private static string KeyOf(Art a) => a.Index is { } i ? $"{a.Name}·{i}" : a.Name;

    private static double Wrap(double value, double mod)
    {
        double wrapped = value % mod;
        return wrapped < 0 ? wrapped + mod : wrapped;
    }

    /// <summary>下一帧的间隔：基准 × (1 ± 抖动)，钳到 [0.15, 4] 秒。</summary>
    private static double NextTick(Random rng, SoakOptions options)
    {
        double dt = options.TickBaseSeconds * (1 + (((rng.NextDouble() * 2) - 1) * options.TickJitter));
        return Math.Clamp(dt, 0.15, 4.0);
    }

    private sealed class Art
    {
        public required string Name { get; init; }

        public int? Index { get; init; }

        public required string Player { get; set; }

        public ArtifactState State { get; set; }

        public double Cooldown { get; set; }

        public int? UsesRemaining { get; set; }

        public int? UsesTotal { get; set; }

        public bool Infinite { get; init; }
    }
}
