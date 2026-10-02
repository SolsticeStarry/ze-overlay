using ZeOverlay.Shared;

namespace ZeOverlay.Stage.Tracking;

/// <summary>S5 实现：行槽位跟踪 + 倒计时外推。</summary>
public sealed class TrackingStage : ITrackingStage
{
    private readonly Tracker _tracker;

    public TrackingStage(Tracker? tracker = null)
    {
        _tracker = tracker ?? new Tracker();
    }

    public string Name => "跟踪";

    /// <summary>当前条目快照（倒计时按墙钟外推），供宿主展示/诊断。</summary>
    public IReadOnlyList<TrackerEntryView> Snapshot(DateTimeOffset now) => _tracker.Snapshot(now);

    public TrackingResult Process(TrackingInput input)
    {
        var observed = new List<ObservedRow>(input.Rows.Count);

        foreach (ParsedRow row in input.Rows)
        {
            observed.Add(new ObservedRow(
                row.Slot,
                row.ArtifactName,
                row.State,
                row.CooldownSeconds,
                row.UsesRemaining,
                row.UsesTotal,
                row.PlayerName));
        }

        TrackerFrameResult result = _tracker.Observe(input.RowCount, observed, input.Now);
        IReadOnlyList<TrackerEntryView> views = _tracker.Snapshot(input.Now);
        return new TrackingResult(result.Page, views, null);
    }
}
