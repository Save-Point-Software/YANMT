using Yanmt.Core.Retention;

namespace Yanmt.Core.Tests.Retention;

public class VacuumPolicyTests
{
    [Theory]
    [InlineData(0, 1000, false)]
    [InlineData(250, 1000, false)] // exactly 25% is not "above"
    [InlineData(251, 1000, true)]
    [InlineData(900, 1000, true)]
    [InlineData(0, 0, false)]
    public void Vacuums_only_above_a_quarter_free(long free, long pages, bool expected) =>
        Assert.Equal(expected, VacuumPolicy.ShouldVacuum(free, pages));
}
