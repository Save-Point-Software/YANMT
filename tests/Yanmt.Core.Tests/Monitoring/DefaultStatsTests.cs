using Yanmt.Core.Charts;
using Yanmt.Core.Monitoring;
using Yanmt.Core.Oids;
using Yanmt.Core.Status;

namespace Yanmt.Core.Tests.Monitoring;

public class DefaultStatsTests
{
    private static readonly Thresholds T = new(80, 90);

    [Theory]
    [InlineData(12, Severity.Normal)]
    [InlineData(80, Severity.Warning)]
    [InlineData(89.9, Severity.Warning)]
    [InlineData(90, Severity.Critical)]
    public void Cpu_thresholds(double percent, Severity expected) =>
        Assert.Equal(expected, DefaultStats.Cpu([(decimal)percent], T).State);

    [Fact]
    public void Cpu_reports_the_busiest_cpu()
    {
        var r = DefaultStats.Cpu([5m, 85m, 20m], T);

        Assert.Equal((Severity.Warning, 85.0, "85%"), (r.State, r.Value!.Value, r.ValueText));
        Assert.Contains("busiest of 3 CPUs", r.Reason);
        Assert.Contains("Warning at 80%", r.Reason);
    }

    [Fact]
    public void Memory_prefers_the_processor_pool_and_hc_counters()
    {
        var usage = DefaultStats.PickMemory(
        [
            new MemoryPool("lsmpi_io", 1000, 1000),
            new MemoryPool("Processor", Used: 1, Free: 1, HcUsed: 3_000_000_000, HcFree: 1_000_000_000),
        ]);

        Assert.Equal(("Processor", 3_000_000_000m, 1_000_000_000m), (usage!.Pool, usage.Used, usage.Free));
        Assert.Equal(75, usage.Percent);
    }

    [Fact]
    public void Memory_falls_back_to_32_bit_counters_and_to_the_first_pool()
    {
        var usage = DefaultStats.PickMemory([new MemoryPool("I/O", 200, 800, HcUsed: 0, HcFree: 0), new MemoryPool("Other", 1, 1)]);

        Assert.Equal(("I/O", 20.0), (usage!.Pool, usage.Percent));
    }

    [Fact]
    public void Memory_with_no_usable_numbers_is_not_available()
    {
        Assert.Null(DefaultStats.PickMemory([]));
        Assert.Null(DefaultStats.PickMemory([new MemoryPool("Processor", null, null), new MemoryPool("Empty", 0, 0)]));
    }

    [Fact]
    public void Memory_reading_explains_the_pool_and_size()
    {
        var r = DefaultStats.Memory(new MemoryUsage("Processor", 3L * 1024 * 1024 * 1024, 1L * 1024 * 1024 * 1024), T);

        Assert.Equal((Severity.Normal, "75%"), (r.State, r.ValueText));
        Assert.Equal("Processor pool 75% used (3 GiB of 4 GiB)", r.Reason);
    }

    [Fact]
    public void Uptime_is_normal_except_on_the_poll_that_sees_a_reboot()
    {
        Assert.Equal((Severity.Normal, "2d 3h 4m"), (DefaultStats.Uptime(18_384_000, false).State, DefaultStats.Uptime(18_384_000, false).ValueText));
        var rebooted = DefaultStats.Uptime(30_000, true);
        Assert.Equal(Severity.Warning, rebooted.State);
        Assert.Equal("rebooted (up 5m 0s)", rebooted.Reason);
    }
}

public class RebootDetectorTests
{
    private const string Engine = "800000090300AABBCCDD0001";

    [Fact]
    public void No_baseline_is_not_a_reboot() => Assert.False(RebootDetector.IsReboot(null, 100, Engine, 7));

    [Fact]
    public void Uptime_going_backwards_is_a_reboot() =>
        Assert.True(RebootDetector.IsReboot(new UptimeBaseline(500_000, Engine, 7), 3_000, Engine, 7));

    [Fact]
    public void Engine_boots_going_up_is_a_reboot_even_if_uptime_looks_fine() =>
        Assert.True(RebootDetector.IsReboot(new UptimeBaseline(100, Engine, 7), 50_000, Engine, 8));

    [Fact]
    public void Uptime_wrapping_after_497_days_is_not_a_reboot() =>
        Assert.False(RebootDetector.IsReboot(new UptimeBaseline(uint.MaxValue - 10_000, Engine, 7), 20_000, Engine, 7));

    [Fact]
    public void A_different_engine_is_a_different_device_not_a_reboot() =>
        Assert.False(RebootDetector.IsReboot(new UptimeBaseline(500_000, Engine, 7), 3_000, "800000090300FFFFFFFF0002", 1));

    [Fact]
    public void Steady_uptime_is_not_a_reboot() =>
        Assert.False(RebootDetector.IsReboot(new UptimeBaseline(500_000, Engine, 7), 530_000, Engine, 7));
}

public class SnmpSettingsTests
{
    [Fact]
    public void Defaults_are_valid() => Assert.Empty(new SnmpSettings().Validate());

    [Theory]
    [InlineData(90, 90)]
    [InlineData(95, 90)]
    [InlineData(0, 90)]
    [InlineData(80, 101)]
    public void Warning_must_be_below_critical_and_in_range(double warning, double critical) =>
        Assert.NotEmpty(new SnmpSettings { CpuWarningPercent = warning, CpuCriticalPercent = critical }.Validate());

    [Fact]
    public void A_timeout_too_long_for_the_interval_is_refused() =>
        Assert.Equal(nameof(SnmpSettings.TimeoutMs), Assert.Single(new SnmpSettings { IntervalSeconds = 60, TimeoutMs = 10_000 }.Validate()).Field);

    [Fact]
    public void Node_overrides_replace_global_values_field_by_field()
    {
        var global = new SnmpSettings().Cpu;

        Assert.Equal(new Thresholds(70, 90), global.Override(70, null));
        Assert.Equal(new Thresholds(80, 95), global.Override(null, 95));
        Assert.Equal(global, global.Override(null, null));
    }
}

public class RateMathTests
{
    [Fact]
    public void Plain_increase() => Assert.Equal(500m, RateMath.Delta(1000, 1500, 64));

    [Fact]
    public void Counter32_wrap_adds_two_to_the_32() => Assert.Equal(300m, RateMath.Delta(4294967196m, 200, 32));

    [Fact]
    public void Counter64_going_down_is_a_reset_not_a_wrap() => Assert.Null(RateMath.Delta(1_000_000, 10, 64));

    [Fact]
    public void Full_range_counter64_delta_is_exact() =>
        Assert.Equal(1m, RateMath.Delta(18446744073709551614m, 18446744073709551615m, 64));

    [Fact]
    public void Bits_per_second_from_octets() => Assert.Equal(8000.0, RateMath.BitsPerSecond(0, 5000, 64, 5));

    [Fact]
    public void Zero_seconds_gives_no_rate() => Assert.Null(RateMath.BitsPerSecond(0, 5000, 64, 0));
}

public class UnitsTests
{
    [Theory]
    [InlineData(0, "0 b/s")]
    [InlineData(950, "950 b/s")]
    [InlineData(1_500, "1.5 kb/s")]
    [InlineData(12_345_678, "12.3 Mb/s")]
    [InlineData(1_000_000_000, "1 Gb/s")]
    public void Bits_per_second(double bps, string text) => Assert.Equal(text, Units.BitsPerSecond(bps));

    [Theory]
    [InlineData(1000, "1 Gb")]
    [InlineData(100, "100 Mb")]
    [InlineData(0, "")]
    public void Interface_speed(double mbps, string text) => Assert.Equal(text, Units.SpeedMbps(mbps));

    [Theory]
    [InlineData(4_500u, "0m 45s")]
    [InlineData(366_000u, "1h 1m")]
    [InlineData(8_640_000u, "1d 0h 0m")]
    public void Uptime(uint ticks, string text) => Assert.Equal(text, Units.Uptime(ticks));

    [Fact]
    public void Bytes_use_binary_prefixes() => Assert.Equal("1.5 GiB", Units.Bytes(1.5 * 1024 * 1024 * 1024));
}

public class DownsamplerTests
{
    private static readonly DateTime T0 = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Buckets_keep_min_avg_and_max()
    {
        Sample[] samples = [new(T0.AddSeconds(10), 1), new(T0.AddSeconds(20), 5), new(T0.AddSeconds(70), 3)];

        var buckets = Downsampler.Downsample(samples, T0, T0.AddMinutes(2), 2);

        Assert.Equal(2, buckets.Count);
        Assert.Equal((1.0, 3.0, 5.0, 2), (buckets[0].Min, buckets[0].Avg, buckets[0].Max, buckets[0].Count));
        Assert.Equal((3.0, 1), (buckets[1].Avg, buckets[1].Count));
        Assert.Equal(T0.AddSeconds(30), buckets[0].Time); // bucket middle
    }

    [Fact]
    public void Empty_buckets_and_out_of_range_samples_are_left_out()
    {
        Sample[] samples = [new(T0.AddMinutes(-5), 9), new(T0.AddSeconds(1), 2), new(T0.AddMinutes(10), 9), new(T0.AddSeconds(5), double.NaN)];

        var buckets = Downsampler.Downsample(samples, T0, T0.AddMinutes(5), 5);

        Assert.Equal(2.0, Assert.Single(buckets).Avg);
    }

    [Fact]
    public void Many_samples_reduce_to_the_bucket_count()
    {
        var samples = Enumerable.Range(0, 10_080).Select(i => new Sample(T0.AddMinutes(i), i % 100));

        Assert.Equal(300, Downsampler.Downsample(samples, T0, T0.AddDays(7), 300).Count);
    }
}

public class OidNamesTests
{
    private static readonly IOidNameResolver Names = BuiltInOidNames.Instance;

    [Theory]
    [InlineData("1.3.6.1.2.1.1.3.0", "sysUpTime.0")]
    [InlineData(".1.3.6.1.2.1.2.2.1.2.10001", "ifDescr.10001")]
    [InlineData("1.3.6.1.2.1.31.1.1.1.18", "ifAlias")]
    [InlineData("1.3.6.1.4.1.9.9.109.1.1.1.1.8.7", "cpmCPUTotal5minRev.7")]
    [InlineData("1.3.6.1.2.1.15.3.1.2.192.0.2.1", "bgpPeerState.192.0.2.1")]
    [InlineData("1.3.6.1.4.1.9.2.1.58.0", "cisco.2.1.58.0")]
    public void Describes_by_longest_known_prefix(string oid, string name) => Assert.Equal(name, Names.Describe(oid));

    [Fact]
    public void Unknown_roots_have_no_name() => Assert.Null(Names.Describe("2.5.4.3"));

    [Theory]
    [InlineData("1.3.6.1.2.1.1.5.0", "1.3.6.1.2.1.1.5.0")]
    [InlineData(".1.3.6.1.2.1.1.5.0", "1.3.6.1.2.1.1.5.0")]
    [InlineData("sysName.0", "1.3.6.1.2.1.1.5.0")]
    [InlineData("ifxtable", "1.3.6.1.2.1.31.1.1")]
    [InlineData("IF-MIB::ifHCInOctets.3", "1.3.6.1.2.1.31.1.1.1.6.3")]
    [InlineData("  bgpPeerState  ", "1.3.6.1.2.1.15.3.1.2")]
    public void Parses_numbers_and_names(string text, string oid)
    {
        Assert.True(Names.TryParse(text, out var parsed));
        Assert.Equal(oid, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("noSuchThing")]
    [InlineData("sysName.zero")]
    [InlineData("1.3.6..1")]
    [InlineData("1.3.6.1; set")]
    public void Refuses_anything_else(string text) => Assert.False(Names.TryParse(text, out _));

    [Fact]
    public void Every_default_stats_oid_has_a_name()
    {
        foreach (var name in new[] { "sysUpTime", "cpmCPUTotal5minRev", "cempMemPoolHCUsed", "cempMemPoolHCFree", "ciscoMemoryPoolUsed", "ifHighSpeed" })
        {
            Assert.True(Names.TryParse(name, out _), name);
        }
    }
}
