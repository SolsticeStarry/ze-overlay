using ZeOverlay.Shared;

namespace ZeOverlay.Tests.Presentation;

public sealed class RowLabelsTests
{
    [Theory]
    [InlineData("滋水枪", false)]
    [InlineData("皇家口粮", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    // 前缀标号
    [InlineData("1 滋水枪", true)]
    [InlineData("12 滋水枪", true)]
    [InlineData("1.滋水枪", true)]
    [InlineData("3、滋水枪", true)]
    [InlineData("【2】滋水枪", true)]
    [InlineData("[3] 滋水枪", true)]
    [InlineData("（4）滋水枪", true)]
    [InlineData("  5滋水枪", true)]
    // 后缀标号（本社区未来适配的形态）
    [InlineData("滋水枪 1", true)]
    [InlineData("滋水枪1", true)]
    [InlineData("滋水枪 12", true)]
    [InlineData("滋水枪【3】", true)]
    [InlineData("滋水枪 ", false)]
    public void HasNumberLabel_DetectsLeadingOrTrailingNumbers(string? name, bool expected)
        => Assert.Equal(expected, RowLabels.HasNumberLabel(name));
}
