using Yanmt.Core.Monitors;
using Yanmt.Core.Status;

namespace Yanmt.Core.Tests.Monitors;

public class PendingTextTests
{
    [Theory]
    [InlineData(Severity.Normal, Severity.Critical, 1, 2, "Critical pending (1 of 2 matching polls before Critical)")]
    [InlineData(Severity.Critical, Severity.Normal, 2, 3, "Normal pending (2 of 3 matching polls before Normal)")]
    [InlineData(Severity.Normal, Severity.Warning, 1, 5, "Warning pending (1 of 5 matching polls before Warning)")]
    [InlineData(Severity.Normal, Severity.Unknown, 1, 2, "Unknown pending (1 of 2 matching polls before Unknown)")]
    [InlineData(Severity.Normal, Severity.Critical, 4, 3, "Critical pending (2 of 3 matching polls before Critical)")] // stale count is capped
    public void Describes_a_pending_change(Severity current, Severity pending, int count, int required, string expected) =>
        Assert.Equal(expected, PendingText.For(current, pending, count, required));

    [Theory]
    [InlineData(Severity.Normal, null, 0, 2)]              // nothing pending
    [InlineData(Severity.Critical, Severity.Critical, 1, 2)] // candidate is already current
    [InlineData(Severity.Normal, Severity.Critical, 1, 1)]  // no dampening
    [InlineData(Severity.Normal, Severity.Critical, 0, 2)]  // no matching poll yet
    public void Nothing_to_show(Severity current, Severity? pending, int count, int required) =>
        Assert.Null(PendingText.For(current, pending, count, required));
}
