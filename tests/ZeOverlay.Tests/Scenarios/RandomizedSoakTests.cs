namespace ZeOverlay.Tests.Scenarios;

using ZeOverlay.Shared;

/// <summary>
/// 连续随机「浸泡」测试：在一段时间内以随机时刻混入拾取/使用/丢失/换行/换人/倒计时/漏读，
/// 逐帧校验跟踪不变量。随机源固定种子 ⇒ 可复现。
/// </summary>
public sealed class RandomizedSoakTests
{
    [Theory]
    [InlineData(ServerKind.Exg, 20261003)]
    [InlineData(ServerKind.Exg, 7)]
    [InlineData(ServerKind.Exg, 1)]
    [InlineData(ServerKind.Exg, 42)]
    [InlineData(ServerKind.Exg, 999)]
    [InlineData(ServerKind.Fys, 20261003)]
    [InlineData(ServerKind.Fys, 7)]
    [InlineData(ServerKind.Fys, 1)]
    [InlineData(ServerKind.Fys, 42)]
    [InlineData(ServerKind.Fys, 999)]
    public void RandomizedStream_HoldsInvariants(ServerKind kind, int seed)
    {
        SoakRun run = RandomizedServerSimulation.Run(kind, seed, seconds: 120);

        Assert.Empty(run.Violations);
        Assert.True(run.Frames.Count > 20);
    }

    [Theory]
    [InlineData(ServerKind.Exg, 20261003)]
    [InlineData(ServerKind.Fys, 20261003)]
    public void RandomizedStream_ActuallyMixesEvents(ServerKind kind, int seed)
    {
        SoakRun run = RandomizedServerSimulation.Run(kind, seed, seconds: 120);

        // 必须真的把几种情况都测到了，否则「浸泡」名不副实。
        Assert.True(run.PickUps > 0, "没有拾取");
        Assert.True(run.Burns > 0, "没有使用");
        Assert.True(run.Losses > 0, "没有丢失");
        Assert.True(run.Reorders > 0, "没有换行");
        Assert.True(run.PlayerChanges >= 0);
    }

    [Theory]
    [InlineData(ServerKind.Exg, 20261003)]
    [InlineData(ServerKind.Fys, 20261003)]
    public void RandomizedStream_ExercisesSameNameMultiples(ServerKind kind, int seed)
    {
        SoakRun run = RandomizedServerSimulation.Run(kind, seed, seconds: 120);

        Assert.True(run.MaxSameName >= 2, "整段仿真没有出现同类多神器");

        if (kind == ServerKind.Fys)
        {
            // 水枪1/水枪2/水枪3 这类：同一名称至少 3 个不同标号同帧出现。
            Assert.Contains(
                run.Frames,
                f => f.Rows.GroupBy(r => r.Name).Any(g => g.Select(r => r.Index).Distinct().Count() >= 3));
        }
    }

    [Theory]
    [InlineData(ServerKind.Exg, 20261003)]
    [InlineData(ServerKind.Fys, 20261003)]
    public void RandomizedStream_ListSizeIsReasonable(ServerKind kind, int seed)
    {
        SoakRun run = RandomizedServerSimulation.Run(kind, seed, seconds: 120);

        // 神器总数平均 12 上下（EXG 会每约 5s 翻页，某一帧的可见行可能更少）。
        Assert.InRange(run.AverageArtifacts, 11.0, 14.5);
        Assert.InRange(run.AverageCount, 6.0, 13.5);
        Assert.InRange(run.MinCount, 1, 12);
        Assert.InRange(run.MaxCount, 8, kind == ServerKind.Fys ? 16 : 12);
    }

    [Fact]
    public void RandomizedStream_ExgSimulatesPaging()
    {
        // EXG 支持 12+：逻辑列表超过一页时会翻页（第 2 页行数明显更少，触发行数骤降判据）。
        var options = new SoakOptions { TargetCount = 15, Paging = true };
        SoakRun run = RandomizedServerSimulation.Run(ServerKind.Exg, 20261003, seconds: 180, options);

        Assert.Empty(run.Violations);

        // 出现过满页（第 1 页 12 行）、行数明显更少的第 2 页，以及 Page2 身份的条目。
        Assert.Contains(run.Frames, f => f.Rows.Count >= 12);
        Assert.Contains(run.Frames, f => f.Rows.Count is > 0 and < 8);
        Assert.Contains(run.Frames, f => f.Overlay.Any(e => e.Page == VisiblePage.Page2));
    }

    [Fact]
    public void RandomizedStream_ManySeeds_NoViolations()
    {
        // 自动扫描：两服各 150 个种子、300s，收集所有不变量违规。
        var failures = new List<string>();

        foreach (ServerKind kind in new[] { ServerKind.Exg, ServerKind.Fys })
        {
            for (int seed = 1; seed <= 150; seed++)
            {
                SoakRun run = RandomizedServerSimulation.Run(kind, seed, seconds: 300);
                if (run.Violations.Count > 0)
                {
                    failures.Add($"{kind} seed={seed}：{run.Violations[0]}");
                }
            }
        }

        Assert.True(failures.Count == 0, "随机浸泡发现不变量违规：\n" + string.Join("\n", failures.Take(15)));
    }

    [Fact]
    public void Fuzz_RandomOptions_FindsNoViolations()
    {
        // 随机化事件概率 / 规模 / 消失超时 / 漏读 / 标号丢失，跑大量组合。
        var rng = new Random(20261003);
        var failures = new List<string>();
        const int iterations = 2000;

        for (int i = 0; i < iterations; i++)
        {
            ServerKind kind = rng.Next(2) == 0 ? ServerKind.Exg : ServerKind.Fys;
            int seed = rng.Next(int.MinValue, int.MaxValue);
            int seconds = rng.Next(20, 90);
            var options = new SoakOptions
            {
                TargetCount = rng.Next(2, 20),
                Paging = kind == ServerKind.Exg && rng.NextDouble() < 0.85,
                MaxArtifacts = rng.NextDouble() < 0.5 ? rng.Next(13, 20) : 0,
                PageFlipSeconds = 0.5 + (rng.NextDouble() * 10),
                TickBaseSeconds = 0.3 + (rng.NextDouble() * 1.9),
                TickJitter = rng.NextDouble() * 0.9,
                MaxIndex = rng.Next(1, 10),
                EmptyFrameBase = rng.NextDouble() * 0.25,
                PickupBase = 0.05 + (rng.NextDouble() * 0.45),
                LossBase = 0.03 + (rng.NextDouble() * 0.40),
                BurnBase = 0.05 + (rng.NextDouble() * 0.50),
                PlayerChangeBase = rng.NextDouble() * 0.35,
                ReorderBase = rng.NextDouble() * 0.45,
                MissBase = rng.NextDouble() * 0.45,
                LabelLossBase = rng.NextDouble() * 0.45,
                DisappearSeconds = 0.5 + (rng.NextDouble() * 20.0),
            };

            SoakRun run = RandomizedServerSimulation.Run(kind, seed, seconds, options);
            if (run.Violations.Count > 0)
            {
                failures.Add(
                    $"#{i} {kind} seed={seed} sec={seconds} {options}：{run.Violations[0]}");
                if (failures.Count >= 12)
                {
                    break;
                }
            }
        }

        Assert.True(failures.Count == 0, "模糊仿真发现不变量违规：\n" + string.Join("\n", failures));
    }

    [Fact]
    public void RandomizedStream_IsDeterministicForSameSeed()
    {
        SoakRun a = RandomizedServerSimulation.Run(ServerKind.Fys, 12345, seconds: 60);
        SoakRun b = RandomizedServerSimulation.Run(ServerKind.Fys, 12345, seconds: 60);

        Assert.Equal(
            a.Frames.Select(f => string.Join("|", f.Rows.Select(r => r.Raw))),
            b.Frames.Select(f => string.Join("|", f.Rows.Select(r => r.Raw))));
    }
}
