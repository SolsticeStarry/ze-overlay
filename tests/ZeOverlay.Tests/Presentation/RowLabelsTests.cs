using ZeOverlay.Shared;

namespace ZeOverlay.Tests.Presentation;

public sealed class RowLabelsTests
{
    [Theory]
    [InlineData("滋水枪", false)]
    [InlineData("皇家口粮", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("1 滋水枪", true)]
    [InlineData("12 滋水枪", true)]
    [InlineData("1.滋水枪", true)]
    [InlineData("3、滋水枪", true)]
    [InlineData("【2】滋水枪", true)]
    [InlineData("[3] 滋水枪", true)]
    [InlineData("（4）滋水枪", true)]
    [InlineData("  5滋水枪", true)]
    public void HasNumberLabel_DetectsLeadingNumbers(string? name, bool expected)
        => Assert.Equal(expected, RowLabels.HasNumberLabel(name));
}
