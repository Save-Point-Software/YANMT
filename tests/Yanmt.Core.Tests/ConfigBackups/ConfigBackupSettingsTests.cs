using Yanmt.Core.ConfigBackups;

namespace Yanmt.Core.Tests.ConfigBackups;

public class ConfigBackupSettingsTests
{
    [Fact]
    public void Defaults_are_valid_and_daily()
    {
        var s = new ConfigBackupSettings();
        Assert.Empty(s.Validate());
        Assert.Equal(86400, s.IntervalSeconds);
    }

    [Theory]
    [InlineData(299)]
    [InlineData(604801)]
    public void Rejects_out_of_range_interval(int seconds) =>
        Assert.Single(new ConfigBackupSettings { IntervalSeconds = seconds }.Validate());

    [Fact]
    public void Rejects_bad_empty_long_and_too_many_patterns()
    {
        Assert.Single(new ConfigBackupSettings { NormalizationPatterns = ["("] }.Validate());
        Assert.Single(new ConfigBackupSettings { NormalizationPatterns = [""] }.Validate());
        Assert.Single(new ConfigBackupSettings { NormalizationPatterns = [new string('a', 201)] }.Validate());
        Assert.NotEmpty(new ConfigBackupSettings { NormalizationPatterns = Enumerable.Repeat("a", 51).ToList() }.Validate());
        Assert.Empty(new ConfigBackupSettings { NormalizationPatterns = ["^ntp server", @"^! Time: .*"] }.Validate());
    }
}
