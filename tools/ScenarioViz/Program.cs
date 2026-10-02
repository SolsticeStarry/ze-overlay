using System.Globalization;
using System.Net;
using System.Text;

using ZeOverlay.Shared;
using ZeOverlay.Tests.Scenarios;

namespace ZeOverlay.Tools.ScenarioViz;

/// <summary>
/// 把 <c>ServerSimulator</c> 跑的 exg / fys 场景逐步导出成一张自包含 HTML 时间线，
/// 便于人工「看着」两种服的假 HUD 帧如何被解析 → 跟踪 → 展示（含增删与倒计时）。
///
/// 用途：<c>dotnet run --project tools/ScenarioViz [输出路径]</c>
/// 默认输出 <c>artifacts/scenario-timeline.html</c>。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string output = args.Length > 0
            ? args[0]
            : Path.Combine("artifacts", "scenario-timeline.html");

        string full = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        List<CapturedScenario> captured = [];
        foreach (VizScenario scenario in Catalog())
        {
            captured.Add(Capture(scenario));
        }

        File.WriteAllText(full, Render(captured), new UTF8Encoding(false));

        Console.WriteLine($"已生成可视化：{full}");
        Console.WriteLine($"场景 {captured.Count} 个，步骤 {captured.Sum(s => s.Steps.Count)} 帧。");

        // 连续随机仿真（同一份随机流也跑在 xUnit 浸泡测试里）。
        SoakRun exg = RandomizedServerSimulation.Run(ServerKind.Exg, 20261003, 120);
        SoakRun fys = RandomizedServerSimulation.Run(ServerKind.Fys, 20261003, 120);

        string simPath = Path.GetFullPath(Path.Combine("artifacts", "scenario-simulation.html"));
        File.WriteAllText(simPath, SoakView.Render(exg, fys), new UTF8Encoding(false));

        Console.WriteLine($"已生成随机仿真：{simPath}");
        Console.WriteLine($"  120s 连续仿真不变量违规：EXG={exg.Violations.Count}，FYS={fys.Violations.Count}。");

        return 0;
    }

    // ---------------------------------------------------------------- 场景脚本

    private static IEnumerable<VizScenario> Catalog()
    {
        string Ready(string name, string player) => $"{name} [R] {player}";
        string Cool(string name, int sec, string player) => $"{name} [{sec}] {player}";
        string P(string name, int idx, string player) => $"{name}{idx}{player}就绪";
        string Pc(string name, int idx, string player, int sec) => $"{name}{idx}{player}{sec}s";

        yield return new VizScenario(
            "EXG", "拾取神器（新增）· 追加到底部",
            "玩家捡起一个新神器，列表底部多出一行。EXG 无服务器标号 ⇒ 行槽位身份，新槽位 = 新条目。",
            [
                new(0, "初始两行", [Ready("皇家口粮", "甲"), Ready("滋水枪", "乙")], null),
                new(1, "捡起「荧光棒」，追加到底部", [Ready("皇家口粮", "甲"), Ready("滋水枪", "乙"), Ready("荧光棒", "丙")],
                    "恰好新增 1 条，总量 3，无移除"),
            ]);

        yield return new VizScenario(
            "EXG", "神器换行 · 新增导致整体下移",
            "新神器插在列表中间，下方整体下移。EXG 按行槽位身份 ⇒ 各槽位内容被重映射；关键是数量正确、无重复条目。",
            [
                new(0, "初始三行", [Ready("皇家口粮", "甲"), Ready("滋水枪", "乙"), Ready("手电筒", "丙")], null),
                new(1, "捡起「荧光棒」插到顶部", [Ready("荧光棒", "丁"), Ready("皇家口粮", "甲"), Ready("滋水枪", "乙"), Ready("手电筒", "丙")],
                    "数量 4、无重复条目"),
            ]);

        yield return new VizScenario(
            "EXG", "神器丢失 · 超时移除",
            "某行从列表消失，未超时先本地外推保留；超过 6 秒无条件超时后移除。",
            [
                new(0, "初始三行", [Ready("皇家口粮", "甲"), Ready("滋水枪", "乙"), Ready("荧光棒", "丙")], null),
                new(2, "底部「荧光棒」消失（未超时）", [Ready("皇家口粮", "甲"), Ready("滋水枪", "乙")],
                    "仍保留，来源转为「推」"),
                new(8, "持续 8 秒没扫到", [Ready("皇家口粮", "甲"), Ready("滋水枪", "乙")],
                    "荧光棒 被移除，总量 2"),
            ]);

        yield return new VizScenario(
            "EXG", "正常使用倒计时 · 外推 + 重校 + 归零",
            "读出冷却 30 秒；中途画面没重读的 5 秒按墙钟外推；再次读到就重校；归零显示为就绪。",
            [
                new(0, "读到 [30]", [Cool("滋水枪", 30, "甲")], "冷却 30"),
                new(5, "墙钟推进 5 秒（未喂新帧）", null, "本地外推 = 25"),
                new(5, "画面再次读到 [24]", [Cool("滋水枪", 24, "甲")], "按画面重校 = 24"),
                new(30, "墙钟推进到 30 秒", null, "归零 ⇒ 状态就绪"),
            ]);

        yield return new VizScenario(
            "EXG", "S4→S6 全链路 · 名单为空全部显示",
            "解析 → 跟踪 → 名单匹配三段串起来；名单为空时匹配器不过滤，展示条目与跟踪条目一致。",
            [
                new(0, "一就绪一冷却", [Ready("皇家口粮", "甲"), Cool("滋水枪", 20, "乙")],
                    "展示 2 条，滋水枪冷却 20"),
            ]);

        yield return new VizScenario(
            "FYS", "拾取神器（新增）",
            "玩家捡起「水枪1」，连写行多出一条。FYS 带服务器标号 ⇒ 「名称+标号」身份。",
            [
                new(0, "初始两行", [P("手电", 1, "熊"), P("定位", 1, "小明")], null),
                new(1, "捡起「水枪1」", [P("手电", 1, "熊"), P("定位", 1, "小明"), P("水枪", 1, "老王")],
                    "恰好新增 1 条，总量 3"),
            ]);

        yield return new VizScenario(
            "FYS", "神器换行 · 新增导致回流",
            "「爆闪1」插到顶部，下面三行整体回流上移。标号身份下回流只是刷新，不是新条目。",
            [
                new(0, "初始三行", [P("手电", 1, "熊"), Pc("定位", 1, "小明", 12), P("水枪", 1, "老王")], null),
                new(1, "捡起「爆闪1」插到顶部", [P("爆闪", 1, "绿光"), P("手电", 1, "熊"), Pc("定位", 1, "小明", 11), P("水枪", 1, "老王")],
                    "仅新增 1 条；手电/定位/水枪 身份不变，定位重校为 11"),
            ]);

        yield return new VizScenario(
            "FYS", "神器换行 · 删除导致回流",
            "「定位1」被用掉，水枪整体上移。定位未超时前仍以「推」保留。",
            [
                new(0, "初始三行", [P("手电", 1, "熊"), Pc("定位", 1, "小明", 12), P("水枪", 1, "老王")], null),
                new(1, "「定位1」被用掉后回流", [P("手电", 1, "熊"), P("水枪", 1, "老王")],
                    "水枪身份不变；定位仍保留（外推），无增无删"),
            ]);

        yield return new VizScenario(
            "FYS", "神器丢失 · 超时移除",
            "「定位1」彻底消失；未超时保留，超 6 秒无条件移除，其余回流条目不受影响。",
            [
                new(0, "初始三行", [P("手电", 1, "熊"), Pc("定位", 1, "小明", 12), P("水枪", 1, "老王")], null),
                new(2, "「定位1」消失（未超时）", [P("手电", 1, "熊"), P("水枪", 1, "老王")], "仍保留"),
                new(8, "持续 8 秒没扫到", [P("手电", 1, "熊"), P("水枪", 1, "老王")],
                    "定位 被移除，总量 2"),
            ]);

        yield return new VizScenario(
            "FYS", "正常使用倒计时 · 外推 + 重校 + 归零",
            "读出 30s 冷却；5 秒后外推 25；再读 24 就重校；归零显示就绪。",
            [
                new(0, "读到 30s", [Pc("水枪", 1, "老王", 30)], "冷却 30"),
                new(5, "墙钟推进 5 秒（未喂新帧）", null, "本地外推 = 25"),
                new(5, "画面再次读到 24s", [Pc("水枪", 1, "老王", 24)], "按画面重校 = 24"),
                new(30, "墙钟推进到 30 秒", null, "归零 ⇒ 状态就绪"),
            ]);

        yield return new VizScenario(
            "FYS", "小数冷却 · 丢弃小数取整",
            "真机样本出现过 `1.5s`；解析器丢弃小数只取整数。",
            [
                new(0, "读到 1.5s", ["定位2雨露之夏1.5s"], "冷却 = 1"),
            ]);

        yield return new VizScenario(
            "FYS", "同类多神器 · 水枪1/水枪2/水枪3",
            "同名不同标号是三条独立条目（身份 = 名称 + 标号）。捡起第 3 把、其中一把进冷却/丢失，都不会合并或串到另两把。",
            [
                new(0, "水枪1 / 水枪2 在场", [P("水枪", 1, "老王"), P("水枪", 2, "屠夫优势图")], null),
                new(1, "捡起水枪3", [P("水枪", 1, "老王"), P("水枪", 2, "屠夫优势图"), P("水枪", 3, "霓光Niko")],
                    "共 3 条，标号 1/2/3 各一条，不重复不合并"),
                new(2, "水枪2 进入冷却", [P("水枪", 1, "老王"), Pc("水枪", 2, "屠夫优势图", 20), P("水枪", 3, "霓光Niko")],
                    "只有水枪·2 变冷却，另两把不受影响"),
            ]);

        yield return new VizScenario(
            "EXG", "同类多神器 · 同名不同玩家",
            "同一把神器可被不同玩家各持一把（真机样本：滋水枪×2、爆闪相机×2）。行槽位身份下两行各自独立更新。",
            [
                new(0, "滋水枪 ×2（不同玩家）", [Ready("滋水枪", "楚门潇"), Ready("滋水枪", "真就一颗a")],
                    "2 条独立条目，各自玩家不同"),
                new(1, "其中一把进入冷却", [Cool("滋水枪", 15, "楚门潇"), Ready("滋水枪", "真就一颗a")],
                    "只有第 1 行变冷却，第 2 行不受影响"),
            ]);

        yield return new VizScenario(
            "EXG", "翻页 · 12 行满页 → 第 2 页",
            "逻辑列表超过一页时，第 2 页行数明显更少（列表顶部锚定），按「行数骤降」判为第 2 页身份。",
            [
                new(0, "第 1 页满页 12 行",
                    [
                        Ready("皇家口粮", "亦陌雕"), Ready("紫色瓶中闪电", "用户6744311"), Ready("爆闪相机", "一个真正的man"),
                        Ready("滋水枪", "Mr.YYYX"), Cool("爆闪相机", 12, "真就一颗a"), Ready("黑色瓶中闪电", "不会狙娱乐"),
                        Ready("手电筒", "游戏尘寰丶"), Cool("灵体定位仪", 20, "小柒Lucky"), Ready("袋装火盐", "街望"),
                        Ready("桶装杏仁水", "豆浆机洗脚"), Ready("滋水枪", "楚门潇"), Ready("荧光棒", "街望"),
                    ],
                    "第 1 页身份（F1），基准 12 行"),
                new(2, "翻到第 2 页（只有 1 行）", [Ready("荧光棒", "新来者")],
                    "行数 12→1 ⇒ 判为第 2 页（F2）；第 1 页条目转为外推、超时前仍显示"),
            ]);
    }

    // ---------------------------------------------------------------- 采集

    private static CapturedScenario Capture(VizScenario scenario)
    {
        var sim = new ServerSimulator(scenario.Server == "FYS" ? ServerKind.Fys : ServerKind.Exg);

        var steps = new List<CapturedStep>();
        IReadOnlyList<EntryState> previous = [];

        foreach (VizStep step in scenario.Steps)
        {
            IReadOnlyList<EntryState> entries;
            bool fed = step.Lines is not null;

            if (step.Lines is { } lines)
            {
                sim.Feed(step.At, lines);
                entries = sim.Snapshot().Select(ToState).ToList();
            }
            else
            {
                entries = sim.SnapshotAt(step.At).Select(ToState).ToList();
            }

            Dictionary<string, EntryState> before = previous.ToDictionary(KeyOf, StringComparer.Ordinal);
            Dictionary<string, EntryState> after = entries.ToDictionary(KeyOf, StringComparer.Ordinal);

            List<string> added = after.Where(kv => !before.ContainsKey(kv.Key)).Select(kv => Label(kv.Value)).ToList();
            List<string> removed = before.Where(kv => !after.ContainsKey(kv.Key)).Select(kv => Label(kv.Value)).ToList();

            steps.Add(new CapturedStep(step.At, step.Note, step.Lines, step.Expect, entries, added, removed, fed));
            previous = entries;
        }

        return new CapturedScenario(scenario.Server, scenario.Title, scenario.Purpose, steps);
    }

    private static EntryState ToState(TrackerEntryView e)
        => new(e.ArtifactName, e.State, e.CooldownSeconds, e.UsesRemaining, e.UsesTotal, e.Page, e.Slot, e.Source, e.ServerIndex);

    private static string KeyOf(EntryState e)
        => e.Index is { } idx
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)e.Page}|{e.Name}|{idx}")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)e.Page}#{e.Slot}");

    private static string Label(EntryState e)
        => e.Index is { } idx ? $"{e.Name}·{idx}" : e.Name;

    // ---------------------------------------------------------------- 渲染

    private static string Render(IReadOnlyList<CapturedScenario> scenarios)
    {
        var html = new StringBuilder();

        html.Append(
            """
            <!DOCTYPE html>
            <html lang="zh-CN">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>ZeOverlay · 双服场景可视化</title>
            <style>
            :root{--bg:#0f1419;--panel:#161d26;--panel2:#1c2632;--line:#2a3947;--txt:#dbe4ee;--dim:#7e90a3;
                  --ready:#3fb950;--cool:#e3a008;--add:#3fb950;--del:#f85149;--accent:#58a6ff;}
            *{box-sizing:border-box}
            body{margin:0;background:var(--bg);color:var(--txt);font:14px/1.5 "Segoe UI","Microsoft YaHei",sans-serif}
            header{padding:18px 24px;border-bottom:1px solid var(--line);background:linear-gradient(180deg,#141b24,#0f1419)}
            header h1{margin:0 0 4px;font-size:19px}
            header p{margin:0;color:var(--dim);font-size:12.5px}
            .layout{display:flex;min-height:calc(100vh - 76px)}
            nav{width:270px;flex:none;border-right:1px solid var(--line);padding:12px 10px;overflow:auto;background:var(--panel)}
            nav h2{margin:12px 8px 6px;font-size:11px;letter-spacing:.12em;color:var(--dim);text-transform:uppercase}
            nav button{display:block;width:100%;text-align:left;background:none;border:1px solid transparent;color:var(--txt);
                       padding:8px 10px;border-radius:8px;cursor:pointer;font-size:13px;margin-bottom:2px}
            nav button:hover{background:var(--panel2)}
            nav button.active{background:#22384f;border-color:var(--accent)}
            main{flex:1;padding:20px 24px;overflow:auto}
            .scenario{display:none;max-width:1180px}
            .scenario.active{display:block}
            .scenario h2{margin:0 0 2px;font-size:17px}
            .scenario .purpose{color:var(--dim);margin:0 0 16px;font-size:13px}
            .step{display:grid;grid-template-columns:88px 1fr;gap:14px;margin-bottom:14px}
            .time{padding-top:10px;text-align:right;color:var(--accent);font-weight:600;font-size:13px}
            .time small{display:block;color:var(--dim);font-weight:400}
            .card{background:var(--panel);border:1px solid var(--line);border-radius:10px;overflow:hidden}
            .card .note{padding:8px 12px;background:var(--panel2);border-bottom:1px solid var(--line);font-size:12.5px;color:#b9c7d6}
            .cols{display:grid;grid-template-columns:minmax(280px,1fr) minmax(320px,1.15fr)}
            .col{padding:10px 12px}
            .col+.col{border-left:1px dashed var(--line)}
            .col h3{margin:0 0 8px;font-size:11px;letter-spacing:.1em;color:var(--dim);text-transform:uppercase}
            .hud{background:#0a0e12;border:1px solid var(--line);border-radius:6px;padding:8px 10px;
                 font:12.5px/1.9 Consolas,"Cascadia Mono",monospace;white-space:pre-wrap;min-height:34px}
            .hud .ln{display:block}
            .hud .ln::before{content:attr(data-i);color:#425166;margin-right:8px}
            .hud.empty{color:var(--dim);font-style:italic}
            table{width:100%;border-collapse:collapse;font-size:12.5px}
            td{padding:4px 6px;border-bottom:1px solid #1f2b38;vertical-align:middle}
            td.num{color:var(--dim);white-space:nowrap;font-family:Consolas,monospace}
            .name{font-weight:600}
            .idx{color:var(--accent);font-weight:600;margin-left:3px}
            .badge{display:inline-block;padding:1px 7px;border-radius:99px;font-size:11.5px;white-space:nowrap}
            .badge.ready{background:rgba(63,185,80,.13);color:var(--ready);border:1px solid rgba(63,185,80,.4)}
            .badge.cool{background:rgba(227,160,8,.13);color:var(--cool);border:1px solid rgba(227,160,8,.45)}
            .badge.unknown{background:#222c36;color:var(--dim);border:1px solid #2f3d4b}
            .src{font-size:11px;color:var(--dim)}
            .uses{color:#c9a7ff;font-size:11.5px}
            .events{margin-top:8px;font-size:12px}
            .events .add{color:var(--add)}
            .events .del{color:var(--del)}
            .expect{margin-top:8px;padding:7px 10px;border-left:3px solid var(--accent);background:#131c26;
                    font-size:12px;color:#a9bccf;border-radius:0 6px 6px 0}
            footer{padding:14px 24px;border-top:1px solid var(--line);color:var(--dim);font-size:12px}
            </style>
            <noscript><style>.scenario{display:block!important}.scenario h2{margin-top:26px}
            nav{display:none}.layout{display:block}</style></noscript>
            </head>
            <body>
            <header>
              <h1>ZeOverlay · 双服场景可视化</h1>
              <p>假 HUD → 真实管线（S4 解析 → S5 跟踪 → S6 匹配）。EXG 括号格式（行槽位身份） / FYS 连写格式（名称+标号身份）。
                 左侧选场景；每张卡片是一帧，左为原始识别行，右为叠加显示结果。</p>
            </header>
            <div class="layout">
            <nav>
            """);

        string? lastServer = null;
        int index = 0;
        foreach (CapturedScenario scenario in scenarios)
        {
            if (scenario.Server != lastServer)
            {
                html.Append("<h2>").Append(WebUtility.HtmlEncode(scenario.Server)).Append(" 社区服</h2>");
                lastServer = scenario.Server;
            }

            html.Append("<button data-i=\"").Append(index).Append("\">")
                .Append(WebUtility.HtmlEncode(scenario.Title)).Append("</button>");
            index++;
        }

        html.Append("</nav><main>");

        index = 0;
        foreach (CapturedScenario scenario in scenarios)
        {
            html.Append("<section class=\"scenario\" data-i=\"").Append(index).Append("\">")
                .Append("<h2>").Append(WebUtility.HtmlEncode(scenario.Server)).Append(" · ")
                .Append(WebUtility.HtmlEncode(scenario.Title)).Append("</h2>")
                .Append("<p class=\"purpose\">").Append(WebUtility.HtmlEncode(scenario.Purpose)).Append("</p>");

            foreach (CapturedStep step in scenario.Steps)
            {
                html.Append("<div class=\"step\">")
                    .Append("<div class=\"time\">t=")
                    .Append(step.At.ToString("0.#", CultureInfo.InvariantCulture)).Append("s<small>")
                    .Append(step.Fed ? "喂帧" : "仅推进").Append("</small></div>")
                    .Append("<div class=\"card\"><div class=\"note\">")
                    .Append(WebUtility.HtmlEncode(step.Note)).Append("</div><div class=\"cols\">");

                // 左：假 HUD 原始行
                html.Append("<div class=\"col\"><h3>识别到的行（假 HUD）</h3>");
                if (step.Lines is { Length: > 0 } lines)
                {
                    html.Append("<div class=\"hud\">");
                    for (int i = 0; i < lines.Length; i++)
                    {
                        html.Append("<span class=\"ln\" data-i=\"").Append(i + 1).Append("\">")
                            .Append(WebUtility.HtmlEncode(lines[i])).Append("</span>");
                    }
                    html.Append("</div>");
                }
                else
                {
                    html.Append("<div class=\"hud empty\">（未喂新帧：仅按墙钟推进取快照）</div>");
                }
                html.Append("</div>");

                // 右：跟踪/叠加结果
                html.Append("<div class=\"col\"><h3>叠加显示（S5 跟踪 → S6）</h3>");
                if (step.Entries.Count == 0)
                {
                    html.Append("<div class=\"hud empty\">（空）</div>");
                }
                else
                {
                    html.Append("<table>");
                    foreach (EntryState e in step.Entries)
                    {
                        html.Append("<tr><td class=\"num\">P").Append((int)e.Page).Append('#').Append(e.Slot).Append("</td>")
                            .Append("<td><span class=\"name\">").Append(WebUtility.HtmlEncode(e.Name)).Append("</span>");
                        if (e.Index is { } idx)
                        {
                            html.Append("<span class=\"idx\">#").Append(idx).Append("</span>");
                        }
                        html.Append("</td><td>").Append(Badge(e)).Append("</td>");
                        html.Append("<td>");
                        if (e is { UsesRemaining: { } r, UsesTotal: { } t })
                        {
                            html.Append("<span class=\"uses\">⚑ ").Append(r).Append('/').Append(t).Append("</span>");
                        }
                        html.Append("</td><td class=\"src\">")
                            .Append(e.Source == EntrySource.Live ? "实" : "推").Append("</td></tr>");
                    }
                    html.Append("</table>");
                }

                if (step.Added.Count > 0 || step.Removed.Count > 0)
                {
                    html.Append("<div class=\"events\">");
                    if (step.Added.Count > 0)
                    {
                        html.Append("<span class=\"add\">＋ 新增 ")
                            .Append(WebUtility.HtmlEncode(string.Join("、", step.Added))).Append("</span> ");
                    }
                    if (step.Removed.Count > 0)
                    {
                        html.Append("<span class=\"del\">－ 移除 ")
                            .Append(WebUtility.HtmlEncode(string.Join("、", step.Removed))).Append("</span>");
                    }
                    html.Append("</div>");
                }

                if (step.Expect is { } expect)
                {
                    html.Append("<div class=\"expect\">预期：").Append(WebUtility.HtmlEncode(expect)).Append("</div>");
                }

                html.Append("</div></div></div></div>");
            }

            html.Append("</section>");
            index++;
        }

        html.Append(
            """
            </main></div>
            <footer>由 <code>tools/ScenarioViz</code> 生成 · 与 <code>tests/ZeOverlay.Tests/Scenarios/*</code> 使用同一份 <code>ServerSimulator</code>。
            倒计时"外推"仅改墙钟、不喂新帧；"实/推"表示该条目最近一次是否为画面实时读到。</footer>
            <script>
            (function(){
              var navs=[].slice.call(document.querySelectorAll('nav button'));
              var secs=[].slice.call(document.querySelectorAll('.scenario'));
              function show(i){
                navs.forEach(function(b,j){b.classList.toggle('active',j===i);});
                secs.forEach(function(s,j){s.classList.toggle('active',j===i);});
                document.querySelector('main').scrollTop=0;
              }
              navs.forEach(function(b){b.addEventListener('click',function(){show(+b.dataset.i);});});
              show(0);
            })();
            </script>
            </body></html>
            """);

        return html.ToString();
    }

    private static string Badge(EntryState e) => e.State switch
    {
        ArtifactState.Cooling => $"<span class=\"badge cool\">冷却 {e.CooldownSeconds?.ToString(CultureInfo.InvariantCulture) ?? "?"}s</span>",
        ArtifactState.Ready => "<span class=\"badge ready\">就绪</span>",
        _ => "<span class=\"badge unknown\">未知</span>",
    };

    // ---------------------------------------------------------------- 模型

    private sealed record VizStep(double At, string Note, string[]? Lines, string? Expect);

    private sealed record VizScenario(string Server, string Title, string Purpose, IReadOnlyList<VizStep> Steps);

    private sealed record EntryState(
        string Name, ArtifactState State, int? CooldownSeconds, int? UsesRemaining, int? UsesTotal,
        VisiblePage Page, int Slot, EntrySource Source, int? Index);

    private sealed record CapturedStep(
        double At, string Note, string[]? Lines, string? Expect,
        IReadOnlyList<EntryState> Entries, IReadOnlyList<string> Added, IReadOnlyList<string> Removed, bool Fed);

    private sealed record CapturedScenario(
        string Server, string Title, string Purpose, IReadOnlyList<CapturedStep> Steps);
}
