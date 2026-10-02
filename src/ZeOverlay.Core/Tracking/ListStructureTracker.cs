using ZeOverlay.Core.Analysis;

namespace ZeOverlay.Core.Tracking;

/// <summary>一帧的列表结构快照。签名用来判断「内容是否整体变了」。</summary>
public sealed record ListStructureSnapshot(int RowCount, double? MedianPitch, string Signature)
{
    public static ListStructureSnapshot FromBands(IReadOnlyList<RowBand> bands, double? medianPitch)
    {
        ArgumentNullException.ThrowIfNull(bands);
        return new ListStructureSnapshot(bands.Count, medianPitch, BuildSignature(bands));
    }

    /// <summary>
    /// 内容签名。**必须量化**（行顶按 2 px、行宽按 8 px 分桶）：
    /// 实测不量化时，抗锯齿会让带宽在 ±2 px 之间抖动，
    /// 结果每一帧都被判成「内容变化」，日志刷屏且不断写盘。
    /// </summary>
    public static string BuildSignature(IReadOnlyList<RowBand> bands)
    {
        ArgumentNullException.ThrowIfNull(bands);

        return string.Join(
            "|",
            bands.Select(b => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{b.Top / 2}:{b.Width / 8}")));
    }
}

public enum ListChangeKind
{
    /// <summary>行数变了。</summary>
    RowCountChanged,

    /// <summary>行数不变但内容变了。</summary>
    ContentChanged,

    /// <summary>两者同时变。</summary>
    RowCountAndContentChanged,
}

/// <summary>一次列表变化。</summary>
public sealed record ListChange(
    ListChangeKind Kind,
    int PreviousRowCount,
    int CurrentRowCount,
    bool LooksLikePageChange);

/// <summary>
/// 列表结构跟踪：判断「翻页」还是「普通内容变化」。
///
/// 依据是实测结论（见 `docs/REAL_SAMPLES.md`）：
/// - 翻页**没有**任何额外视觉信号（无页码、无高亮），只能靠内容/行数变化检测；
/// - **第 2 页行数显著少于第 1 页**，行数骤降是最可靠的主信号；
/// - 翻页周期不固定，不能定时假设。
///
/// 因此这里维护一个「基准行数」（观测到的最大值 ≈ 第 1 页规模），
/// 当行数**明显低于**基准且是下降方向时，判为疑似翻页。
/// 注意：玩家消耗/丢武器也会减少行数，所以这只是「疑似」，最终仍需内容层面确认。
/// </summary>
public sealed class ListStructureTracker
{
    /// <summary>行数降到基准的这个比例以下，才认为「显著少」。取实测第 2 页明显偏少的保守值。</summary>
    private const double PageTwoRowRatioThreshold = 0.6;

    private string? _signature;
    private int _rowCount = -1;

    /// <summary>观测到的最大行数，近似「第 1 页规模」。</summary>
    public int BaselineRowCount { get; private set; }

    public int Observations { get; private set; }

    public bool HasBaseline => _signature is not null;

    /// <summary>喂入一帧快照；无变化返回 null。</summary>
    public ListChange? Observe(ListStructureSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Observations++;

        if (_signature is null)
        {
            _signature = snapshot.Signature;
            _rowCount = snapshot.RowCount;
            BaselineRowCount = snapshot.RowCount;
            return null;
        }

        bool rowCountChanged = snapshot.RowCount != _rowCount;
        bool contentChanged = !string.Equals(snapshot.Signature, _signature, StringComparison.Ordinal);

        if (!rowCountChanged && !contentChanged)
        {
            return null;
        }

        int previousRowCount = _rowCount;
        int baselineBefore = BaselineRowCount;

        _signature = snapshot.Signature;
        _rowCount = snapshot.RowCount;
        BaselineRowCount = Math.Max(BaselineRowCount, snapshot.RowCount);

        ListChangeKind kind = (rowCountChanged, contentChanged) switch
        {
            (true, true) => ListChangeKind.RowCountAndContentChanged,
            (true, false) => ListChangeKind.RowCountChanged,
            _ => ListChangeKind.ContentChanged,
        };

        // 疑似翻页：行数下降，且落到基准行数的显著比例以下。
        bool looksLikePageChange = rowCountChanged
            && snapshot.RowCount < previousRowCount
            && baselineBefore > 0
            && snapshot.RowCount <= baselineBefore * PageTwoRowRatioThreshold;

        return new ListChange(kind, previousRowCount, snapshot.RowCount, looksLikePageChange);
    }
}
