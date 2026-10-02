using ZeOverlay.Shared;

namespace ZeOverlay.Infrastructure;

/// <summary>
/// 服务器档案的默认值、迁移与转换。
///
/// 迁移策略：旧版本只有全局 <see cref="AppConfig.Roi"/> + <c>watchlist.json</c>。
/// 首次读到「无档案」的配置时，把它们落成一个 <c>default</c>（本服）档案，
/// 并**额外预置**一个 <c>community</c>（社区服，Plain 语法、不翻页）档案——
/// 后者 ROI 为空，自动选档会跳过它，直到用户切过去框选一次。
/// </summary>
public static class ProfileDefaults
{
    public const string DefaultProfileId = "default";
    public const string CommunityProfileId = "community";

    /// <summary>本服（括号语法）的预置名称表，来自 <c>docs/REAL_SAMPLES.md</c>。</summary>
    public static readonly string[] BracketVocabulary =
    [
        "皇家口粮", "紫色瓶中闪电", "爆闪相机", "滋水枪", "黑色瓶中闪电",
        "手电筒", "灵体定位仪", "袋装火盐", "桶装杏仁水", "荧光棒",
    ];

    /// <summary>社区服（Plain 语法）的预置名称表，来自真机样本 #5 / #6。</summary>
    public static readonly string[] PlainVocabulary =
    [
        "人闪", "口粮", "大火盐", "大荧光", "定位", "手电", "桶装水", "水枪", "黑闪", "爆闪",
    ];

    /// <summary>解析档案的语法字符串；无法识别时按本服括号语法处理。</summary>
    public static ParserMode ParseMode(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "plain" => ParserMode.Plain,
        _ => ParserMode.Bracket,
    };

    /// <summary>把档案的名单转成设置面板/匹配器使用的 <see cref="WatchlistConfig"/>（拷贝）。</summary>
    public static WatchlistConfig ToWatchlist(ServerProfileConfig profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new WatchlistConfig
        {
            Names = profile.Watchlist.ToList(),
            SortOrder = profile.SortOrder.ToList(),
            MatchThreshold = profile.MatchThreshold,
        };
    }

    /// <summary>把设置面板返回的名单写回档案。</summary>
    public static void ApplyWatchlist(ServerProfileConfig profile, WatchlistConfig watchlist)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(watchlist);
        profile.Watchlist = watchlist.Names.ToList();
        profile.SortOrder = watchlist.SortOrder.ToList();
        profile.MatchThreshold = watchlist.MatchThreshold;
    }

    /// <summary>取当前生效档案；Id 失效时回退到第一个，档案表为空时返回 null。</summary>
    public static ServerProfileConfig? Active(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.Profiles.Count == 0)
        {
            return null;
        }

        return config.Profiles.FirstOrDefault(p => string.Equals(p.Id, config.ActiveProfileId, StringComparison.Ordinal))
            ?? config.Profiles[0];
    }

    /// <summary>
    /// 把旧的「多档案」结构折叠成单配置：采用当前档案的 ROI / 名单，并合并所有档案的名表。
    /// 行语法运行时逐行自动判断，不再需要档案；折叠后清空 <see cref="AppConfig.Profiles"/>。
    /// </summary>
    public static void CollapseProfiles(AppConfig config, WatchlistConfig watchlist)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(watchlist);

        if (config.Profiles.Count > 0)
        {
            ServerProfileConfig active = Active(config)!;

            if (active.Roi.IsSet)
            {
                config.Roi = active.Roi;
            }

            if (active.Watchlist.Count > 0)
            {
                watchlist.Names = active.Watchlist.ToList();
                watchlist.SortOrder = active.SortOrder.ToList();
                watchlist.MatchThreshold = active.MatchThreshold;
            }

            var vocabulary = new List<string>(config.Vocabulary);
            foreach (ServerProfileConfig profile in config.Profiles)
            {
                vocabulary.AddRange(profile.Vocabulary);
                vocabulary.AddRange(profile.Watchlist);
            }

            config.Vocabulary = vocabulary
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            config.Profiles.Clear();
            config.ActiveProfileId = string.Empty;
        }

        // 新装/名表为空时给一份预置名表（两服并集），否则社区服连写行切不出名称。
        if (config.Vocabulary.Count == 0)
        {
            config.Vocabulary = BracketVocabulary.Concat(PlainVocabulary).Distinct(StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>
    /// 保证配置里有可用的档案表。旧的「全局 ROI + watchlist.json」会迁移成 <c>default</c> 档案，
    /// 并预置一个社区服档案。幂等：已有档案时只做 Id/ActiveProfileId 的修正。
    /// </summary>
    public static ServerProfileConfig EnsureProfiles(AppConfig config, WatchlistConfig watchlist)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(watchlist);

        if (config.Profiles.Count == 0)
        {
            config.Profiles.Add(new ServerProfileConfig
            {
                Id = DefaultProfileId,
                DisplayName = "本服",
                ParserMode = "bracket",
                PagingEnabled = true,
                MaxRowsPerPage = ListRules.MaxRowsPerPage,
                Roi = config.Roi,
                Watchlist = watchlist.Names.ToList(),
                SortOrder = watchlist.SortOrder.ToList(),
                MatchThreshold = watchlist.MatchThreshold,
                Vocabulary = BracketVocabulary.ToList(),
            });

            config.Profiles.Add(new ServerProfileConfig
            {
                Id = CommunityProfileId,
                DisplayName = "社区服(entWatch)",
                ParserMode = "plain",
                PagingEnabled = false,
                MaxRowsPerPage = 16,
                // 社区服列表变化快（神器随时被捡起/用掉）⇒ 提高采集/识别频率让新行尽快替换旧行。
                // 行消失超时仍沿用全局 6s（用户要求保留粘滞）。
                CaptureFps = 5,
                RecognitionIntervalMs = 200,
                RowDisappearSeconds = 0,
                Roi = new RoiConfig(),
                Watchlist = [],
                SortOrder = [],
                MatchThreshold = watchlist.MatchThreshold,
                Vocabulary = PlainVocabulary.ToList(),
            });
        }

        // 修正空 Id（手改配置可能漏填）。
        for (int i = 0; i < config.Profiles.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(config.Profiles[i].Id))
            {
                config.Profiles[i].Id = i == 0 ? DefaultProfileId : $"profile{i + 1}";
            }
        }

        // 已有配置里的 Plain 档案若从没设过采集频率，补上跟手默认值（不覆盖已显式配置的）。
        foreach (ServerProfileConfig profile in config.Profiles)
        {
            if (ParseMode(profile.ParserMode) == ParserMode.Plain && profile.CaptureFps == 0)
            {
                profile.CaptureFps = 5;
                profile.RecognitionIntervalMs = 200;
                profile.RowDisappearSeconds = 0;
            }
        }

        if (Active(config) is { } active)
        {
            config.ActiveProfileId = active.Id;
        }

        return Active(config)!;
    }
}
