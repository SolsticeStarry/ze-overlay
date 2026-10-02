using ZeOverlay.Shared;

namespace ZeOverlay.Tests.Tracking;

/// <summary>
/// 跟踪条目稳定键：标号身份（连写服）必须用「名称+标号」，不能用「页#槽位」——
/// 回流会保留"上次所在行位"，不同条目会撞同一行位，用它当键会让名单匹配张冠李戴。
/// </summary>
public sealed class TrackerKeysTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static TrackerEntryView Entry(VisiblePage page, int slot, string name, int? index)
        => new(
            page,
            slot,
            name,
            ArtifactState.Ready,
            null,
            null,
            null,
            EntrySource.Live,
            T0,
            T0,
            0,
            string.Empty,
            index);

    [Fact]
    public void LabelIdentity_IsUniqueEvenWhenSlotsCollide()
    {
        // 定位1 / 定位2 回流后都停在行位 2 ⇒ 页#槽位相同，但身份必须不同。
        TrackerEntryView a = Entry(VisiblePage.Page1, 2, "定位", 1);
        TrackerEntryView b = Entry(VisiblePage.Page1, 2, "定位", 2);

        Assert.False(string.Equals(TrackerKeys.Identity(a), TrackerKeys.Identity(b), StringComparison.Ordinal));
    }

    [Fact]
    public void SlotIdentity_UsesPageAndSlot()
    {
        // 括号服（无标号）：同名不同槽位是两条不同条目。
        TrackerEntryView a = Entry(VisiblePage.Page1, 2, "滋水枪", null);
        TrackerEntryView b = Entry(VisiblePage.Page1, 3, "滋水枪", null);

        Assert.False(string.Equals(TrackerKeys.Identity(a), TrackerKeys.Identity(b), StringComparison.Ordinal));
    }

    [Fact]
    public void SameEntry_YieldsSameKey()
    {
        TrackerEntryView a = Entry(VisiblePage.Page1, 5, "手电", 2);
        TrackerEntryView b = Entry(VisiblePage.Page1, 5, "手电", 2);

        Assert.Equal(TrackerKeys.Identity(a), TrackerKeys.Identity(b));
    }
}
