using Yanmt.Core.Status;

namespace Yanmt.Core.Tests.Status;

public class StatusRollupTests
{
    [Fact]
    public void Rank_orders_Critical_over_Warning_over_Unknown_over_Normal()
    {
        Assert.True(Severity.Critical.Rank() > Severity.Warning.Rank());
        Assert.True(Severity.Warning.Rank() > Severity.Unknown.Rank());
        Assert.True(Severity.Unknown.Rank() > Severity.Normal.Rank());
    }

    [Fact]
    public void Nothing_reporting_is_Unknown()
    {
        Assert.Equal(Severity.Unknown, StatusRollup.Worst([]));
    }

    [Theory]
    [InlineData(new[] { Severity.Normal }, Severity.Normal)]
    [InlineData(new[] { Severity.Normal, Severity.Normal }, Severity.Normal)]
    [InlineData(new[] { Severity.Normal, Severity.Unknown }, Severity.Unknown)]
    [InlineData(new[] { Severity.Unknown, Severity.Warning }, Severity.Warning)]
    [InlineData(new[] { Severity.Warning, Severity.Normal, Severity.Critical }, Severity.Critical)]
    [InlineData(new[] { Severity.Critical, Severity.Unknown }, Severity.Critical)]
    public void Worst_state_wins(Severity[] states, Severity expected)
    {
        Assert.Equal(expected, StatusRollup.Worst(states));
    }

    [Fact]
    public void WorseOf_is_symmetric()
    {
        foreach (var a in Enum.GetValues<Severity>())
        {
            foreach (var b in Enum.GetValues<Severity>())
            {
                Assert.Equal(a.WorseOf(b), b.WorseOf(a));
            }
        }
    }
}
