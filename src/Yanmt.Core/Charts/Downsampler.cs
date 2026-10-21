namespace Yanmt.Core.Charts;

public readonly record struct Sample(DateTime Time, double Value);

/// <summary>One time bucket of a downsampled series. Charts draw Avg as the line and Min..Max as a band.</summary>
public readonly record struct Bucket(DateTime Time, double Min, double Avg, double Max, int Count);

/// <summary>
/// Reduces a series to at most a fixed number of points for drawing, keeping each bucket's min
/// and max so spikes survive. Empty buckets are left out; the chart breaks the line across gaps.
/// </summary>
public static class Downsampler
{
    public static IReadOnlyList<Bucket> Downsample(IEnumerable<Sample> samples, DateTime from, DateTime to, int buckets)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(buckets, 1);
        if (to <= from)
        {
            return [];
        }

        var width = (to - from).Ticks / (double)buckets;
        var acc = new (double Min, double Max, double Sum, int Count)[buckets];
        foreach (var s in samples)
        {
            if (s.Time < from || s.Time > to || !double.IsFinite(s.Value))
            {
                continue;
            }

            var i = Math.Min(buckets - 1, (int)((s.Time - from).Ticks / width));
            ref var a = ref acc[i];
            a = a.Count == 0
                ? (s.Value, s.Value, s.Value, 1)
                : (Math.Min(a.Min, s.Value), Math.Max(a.Max, s.Value), a.Sum + s.Value, a.Count + 1);
        }

        var result = new List<Bucket>();
        for (var i = 0; i < buckets; i++)
        {
            var a = acc[i];
            if (a.Count > 0)
            {
                result.Add(new Bucket(from + TimeSpan.FromTicks((long)(width * (i + 0.5))), a.Min, a.Sum / a.Count, a.Max, a.Count));
            }
        }

        return result;
    }
}
