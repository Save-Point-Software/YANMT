using Yanmt.Core.Retention;

namespace Yanmt.Core.Tests.Retention;

public class RetentionSettingsTests
{
    [Fact]
    public void Defaults_match_the_spec_and_are_valid()
    {
        var s = new RetentionSettings();
        Assert.Equal(7, s.RawSampleDays);
        Assert.Equal(90, s.RollupDays);
        Assert.Equal(365, s.EventDays);
        Assert.Null(s.KeepConfigVersions);
        Assert.Empty(s.Validate());
    }

    [Theory]
    [InlineData(0, 90, 365, null)]
    [InlineData(7, 3651, 365, null)]
    [InlineData(7, 90, 0, null)]
    [InlineData(7, 90, 365, 0)]
    [InlineData(7, 90, 365, 1001)]
    public void Out_of_range_values_are_rejected(int raw, int rollup, int events, int? keep)
    {
        var s = new RetentionSettings { RawSampleDays = raw, RollupDays = rollup, EventDays = events, KeepConfigVersions = keep };
        Assert.NotEmpty(s.Validate());
    }

    [Fact]
    public void Raw_cutoff_uses_retention_when_rollups_are_current()
    {
        var now = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var cutoff = new RetentionSettings().RawPurgeCutoff(now, now.AddHours(-2));
        Assert.Equal(now.AddDays(-7), cutoff);
    }

    [Fact]
    public void Raw_cutoff_never_passes_the_newest_rolled_hour()
    {
        var now = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var rolled = now.AddDays(-9);
        Assert.Equal(rolled, new RetentionSettings().RawPurgeCutoff(now, rolled));
    }

    [Fact]
    public void Raw_cutoff_is_null_when_nothing_has_been_rolled_up()
    {
        Assert.Null(new RetentionSettings().RawPurgeCutoff(DateTime.UtcNow, null));
    }
}
