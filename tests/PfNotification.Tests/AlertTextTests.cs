using PfNotification.Party;

namespace PfNotification.Tests;

public sealed class AlertTextTests
{
    [Theory]
    [InlineData(new[] { "WHM" }, new string[0], "WHM joined")]
    [InlineData(new string[0], new[] { "SAM" }, "SAM left")]
    [InlineData(new[] { "WHM", "DRG" }, new[] { "SAM" }, "SAM left, WHM and DRG joined")]
    [InlineData(new[] { "WHM", "DRG", "PLD" }, new string[0], "WHM, DRG and PLD joined")]
    [InlineData(new[] { "" }, new string[0], "someone joined")]
    [InlineData(new string[0], new string[0], "Party changed")]
    public void ChangeTitles(string[] joined, string[] left, string expected)
    {
        Assert.Equal(expected, AlertText.ChangeTitle(joined, left));
    }

    [Fact]
    public void SummaryHasCountAndRoles()
    {
        Assert.Equal("5/8: 1 tank, 1 healer, 3 DPS", AlertText.Summary(5, 8, new RoleCounts(1, 1, 3, 0)));
    }
}
