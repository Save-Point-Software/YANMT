using Yanmt.Core.Monitors;

namespace Yanmt.Core.Tests.Monitors;

public class MonitorTimingTests
{
    [Theory]
    [InlineData(60, 2, 120)]
    [InlineData(300, 2, 600)]
    [InlineData(300, 1, 300)]
    [InlineData(60, 0, 60)] // 0 matches behaves like 1: the state changes on the first poll
    public void Worst_case_is_interval_times_matches(int interval, int matches, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), MonitorTiming.WorstCaseDetection(interval, matches));

    [Theory]
    [InlineData(60, 2, "up to 2 min (2 × 60 s)")]
    [InlineData(300, 2, "up to 10 min (2 × 300 s)")]
    [InlineData(30, 1, "up to 30 s (1 × 30 s)")]
    [InlineData(45, 2, "up to 1 min 30 s (2 × 45 s)")]
    [InlineData(3600, 3, "up to 3 h (3 × 3600 s)")]
    [InlineData(1800, 3, "up to 1 h 30 min (3 × 1800 s)")]
    public void Describes_detection_time(int interval, int matches, string expected) =>
        Assert.Equal(expected, MonitorTiming.DescribeDetection(interval, matches));
}
