namespace Yanmt.Core.Rollups;

/// <summary>Min/avg/max of the numeric samples in one hour.</summary>
public sealed record MetricRollup(double Min, double Avg, double Max, int Count);

/// <summary>One ICMP poll as the rollup sees it.</summary>
public readonly record struct IcmpPoint(int Sent, int Received, double? MinMs, double? AvgMs, double? MaxMs);

/// <summary>ICMP latency and loss over one hour. Latencies are null when no echo was answered.</summary>
public sealed record IcmpRollup(int Sent, int Received, double? MinMs, double? AvgMs, double? MaxMs, double LossPercent, int SampleCount);

/// <summary>Pure rollup math for the hourly tables.</summary>
public static class HourlyRollup
{
    public static DateTime HourStart(DateTime utc) =>
        new(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);

    public static MetricRollup? Metric(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0) return null;
        double min = double.MaxValue, max = double.MinValue, sum = 0;
        foreach (var v in values)
        {
            if (v < min) min = v;
            if (v > max) max = v;
            sum += v;
        }
        return new MetricRollup(min, sum / values.Count, max, values.Count);
    }

    /// <summary>Min of mins, max of maxes, average weighted by answered echoes, loss over all echoes sent.</summary>
    public static IcmpRollup? Icmp(IReadOnlyCollection<IcmpPoint> points)
    {
        if (points.Count == 0) return null;
        int sent = 0, received = 0;
        double? min = null, max = null;
        double weighted = 0;
        int weight = 0;
        foreach (var p in points)
        {
            sent += p.Sent;
            received += p.Received;
            if (p.MinMs is { } lo) min = min is null ? lo : Math.Min(min.Value, lo);
            if (p.MaxMs is { } hi) max = max is null ? hi : Math.Max(max.Value, hi);
            if (p.AvgMs is { } avg && p.Received > 0)
            {
                weighted += avg * p.Received;
                weight += p.Received;
            }
        }
        double? mean = weight > 0 ? weighted / weight : null;
        double loss = sent > 0 ? 100.0 * (sent - received) / sent : 0;
        return new IcmpRollup(sent, received, min, mean, max, loss, points.Count);
    }
}
