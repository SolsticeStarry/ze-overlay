namespace ZeOverlay.Shared;

/// <summary>管线阶段：输入 → 输出。有状态的阶段（缓存 / 会话 / 跟踪表）把状态放字段。</summary>
public interface IStage<in TIn, out TOut>
{
    string Name { get; }

    TOut Process(TIn input);
}

/// <summary>管线一次运行的产出：行分析报告 + 待展示条目。</summary>
public sealed record PipelineStepResult(RowReport Report, IReadOnlyList<DisplayEntry> Entries);
