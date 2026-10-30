using Yanmt.Core.Rollups;

namespace Yanmt.Core.Tests.Rollups;

public class HourlyRollupTests
{
    [Fact]
    public void HourStart_truncates_to_the_hour()
    {
        var t = new DateTime(2026, 3, 4, 5, 59, 59, 999, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 3, 4, 5, 0, 0, DateTimeKind.Utc), HourlyRollup.HourStart(t));
    }

    [Fact]
    public void Metric_empty_is_null() => Assert.Null(HourlyRollup.Metric([]));

    [Fact]
    public void Metric_min_avg_max()
    {
        var r = HourlyRollup.Metric([10, 20, 60])!;
        Assert.Equal(10, r.Min);
        Assert.Equal(30, r.Avg);
        Assert.Equal(60, r.Max);
        Assert.Equal(3, r.Count);
    }

    [Fact]
    public void Icmp_empty_is_null() => Assert.Null(HourlyRollup.Icmp([]));

    [Fact]
    public void Icmp_weights_average_by_answered_echoes_and_sums_loss()
    {
        var r = HourlyRollup.Icmp([
            new(4, 4, 1, 2, 5),
            new(4, 2, 3, 6, 9),
        ])!;
        Assert.Equal(8, r.Sent);
        Assert.Equal(6, r.Received);
        Assert.Equal(1, r.MinMs);
        Assert.Equal(9, r.MaxMs);
        Assert.Equal((2 * 4 + 6 * 2) / 6.0, r.AvgMs!.Value, 6);
        Assert.Equal(25, r.LossPercent, 6);
        Assert.Equal(2, r.SampleCount);
    }

    [Fact]
    public void Icmp_total_loss_has_null_latencies()
    {
        var r = HourlyRollup.Icmp([new(4, 0, null, null, null), new(4, 0, null, null, null)])!;
        Assert.Null(r.MinMs);
        Assert.Null(r.AvgMs);
        Assert.Null(r.MaxMs);
        Assert.Equal(100, r.LossPercent);
    }

    [Fact]
    public void Icmp_ignores_all_lost_polls_for_latency()
    {
        var r = HourlyRollup.Icmp([new(4, 0, null, null, null), new(4, 4, 2, 3, 4)])!;
        Assert.Equal(3, r.AvgMs);
        Assert.Equal(50, r.LossPercent);
    }
}
