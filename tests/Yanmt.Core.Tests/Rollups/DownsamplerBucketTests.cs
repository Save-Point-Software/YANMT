using Yanmt.Core.Charts;

namespace Yanmt.Core.Tests.Rollups;

public class DownsamplerBucketTests
{
    private static readonly DateTime From = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Weights_the_average_by_count_and_keeps_min_and_max()
    {
        var points = new[]
        {
            new Bucket(From.AddMinutes(30), 10, 20, 40, 60), // an hourly rollup
            new Bucket(From.AddMinutes(40), 100, 100, 100, 1), // a raw sample in the same bucket
        };

        var result = Downsampler.Downsample(points, From, From.AddDays(1), 1);

        var b = Assert.Single(result);
        Assert.Equal(10, b.Min);
        Assert.Equal(100, b.Max);
        Assert.Equal((20 * 60 + 100) / 61.0, b.Avg, 6);
        Assert.Equal(61, b.Count);
    }

    [Fact]
    public void Drops_points_outside_the_range_and_empty_buckets()
    {
        var points = new[]
        {
            new Bucket(From.AddDays(-1), 1, 1, 1, 1),
            new Bucket(From.AddHours(1), 5, 5, 5, 1),
            new Bucket(From.AddHours(23), 7, 7, 7, 1),
        };

        var result = Downsampler.Downsample(points, From, From.AddDays(1), 24);

        Assert.Equal(2, result.Count);
    }
}
