using Microsoft.EntityFrameworkCore;
using Yanmt.Core.Charts;
using Yanmt.Core.Monitoring;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Data.Entities;
using Yanmt.Infrastructure.Settings;

namespace Yanmt.Infrastructure.Monitoring;

public sealed record NodeSystemView(
    string? SysDescr,
    string? SysObjectId,
    string? SysName,
    string? SysLocation,
    string? SysContact,
    string? EngineId,
    int? EngineBoots,
    string? MemoryPool,
    DateTime? LastPollUtc,
    DateTime? LastSuccessUtc,
    DateTime? LastRebootUtc);

/// <summary>Chart time ranges offered on the node page.</summary>
public enum ChartRange
{
    Hour,
    SixHours,
    Day,
    Week,
    Month,
    Quarter,
}

public sealed record NodeCharts(
    DateTime FromUtc,
    DateTime ToUtc,
    IReadOnlyList<Bucket> Latency,
    IReadOnlyList<Bucket> Loss,
    IReadOnlyList<Bucket> Cpu,
    IReadOnlyList<Bucket> Memory);

/// <summary>Read side of the node detail page: system info and downsampled chart series.</summary>
public sealed class NodeDetailService(IDbContextFactory<YanmtDbContext> dbFactory, SettingsService settings, TimeProvider clock)
{
    /// <summary>The CPU and memory thresholds in force for a node (its overrides, else the global settings).</summary>
    public async Task<(Thresholds Cpu, Thresholds Memory, int SnmpIntervalSeconds, int IcmpIntervalSeconds)> GetPollingAsync(
        int nodeId, CancellationToken cancellationToken = default)
    {
        var snmp = await settings.GetAsync(SettingKeys.Snmp, cancellationToken);
        var icmp = await settings.GetAsync(SettingKeys.Icmp, cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var n = await db.Nodes.AsNoTracking().Where(x => x.Id == nodeId)
            .Select(x => new { x.CpuWarningPercent, x.CpuCriticalPercent, x.MemoryWarningPercent, x.MemoryCriticalPercent })
            .FirstOrDefaultAsync(cancellationToken);
        return (snmp.Cpu.Override(n?.CpuWarningPercent, n?.CpuCriticalPercent),
            snmp.Memory.Override(n?.MemoryWarningPercent, n?.MemoryCriticalPercent),
            snmp.IntervalSeconds,
            icmp.IntervalSeconds);
    }

    /// <summary>About 300 points per chart, whatever the range.</summary>
    public const int ChartPoints = 300;

    public static TimeSpan Span(ChartRange range) => range switch
    {
        ChartRange.Hour => TimeSpan.FromHours(1),
        ChartRange.SixHours => TimeSpan.FromHours(6),
        ChartRange.Day => TimeSpan.FromDays(1),
        ChartRange.Week => TimeSpan.FromDays(7),
        ChartRange.Month => TimeSpan.FromDays(30),
        _ => TimeSpan.FromDays(90),
    };

    public async Task<NodeSystemView?> GetSystemAsync(int nodeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.NodeSystemInfo.AsNoTracking()
            .Where(i => i.NodeId == nodeId)
            .Select(i => new NodeSystemView(i.SysDescr, i.SysObjectId, i.SysName, i.SysLocation, i.SysContact, i.EngineId, i.EngineBoots,
                i.MemoryPool, i.LastPollUtc, i.LastSuccessUtc, i.LastRebootUtc))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Trend per numeric source of a node (CPU, memory, custom monitors) over the range (default 24 h), keyed by source type and key.</summary>
    public async Task<(DateTime From, DateTime To, Dictionary<(StatusSourceType Type, string Key), IReadOnlyList<Bucket>> Trends)> GetTrendsAsync(
        int nodeId, ChartRange range = ChartRange.Day, CancellationToken cancellationToken = default)
    {
        var to = clock.GetUtcNow().UtcDateTime;
        var from = to - Span(range);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var sources = await db.StatusSources.AsNoTracking().Where(s => s.NodeId == nodeId)
            .Select(s => new { s.Id, s.SourceType, s.SourceKey }).ToListAsync(cancellationToken);
        var trends = new Dictionary<(StatusSourceType Type, string Key), IReadOnlyList<Bucket>>();
        foreach (var s in sources)
        {
            var points = await MetricPointsAsync(db, s.Id, from, to, range, cancellationToken);
            if (points.Count > 0)
            {
                trends[(s.SourceType, s.SourceKey)] = Downsampler.Downsample(points, from, to, range >= ChartRange.Month ? ChartPoints : 60);
            }
        }

        return (from, to, trends);
    }

    /// <summary>Ranges longer than the raw window read hourly rollups; shorter ones read raw samples only.</summary>
    private static bool UsesRollups(ChartRange range) => range >= ChartRange.Month;

    /// <summary>
    /// Points for one metric source. Long ranges use hourly rollups up to the newest rolled hour, then raw samples
    /// after it (the hours not rolled up yet), so nothing is counted twice.
    /// </summary>
    private static async Task<List<Bucket>> MetricPointsAsync(
        YanmtDbContext db, long sourceId, DateTime from, DateTime to, ChartRange range, CancellationToken ct)
    {
        var points = new List<Bucket>();
        var rawFrom = from;
        if (UsesRollups(range))
        {
            var rollups = await db.MetricRollupHours.AsNoTracking()
                .Where(r => r.StatusSourceId == sourceId && r.HourUtc >= from.AddHours(-1) && r.HourUtc < to)
                .Select(r => new { r.HourUtc, r.Min, r.Avg, r.Max, r.SampleCount })
                .ToListAsync(ct);
            points.AddRange(rollups.Select(r => new Bucket(r.HourUtc.AddMinutes(30), r.Min, r.Avg, r.Max, r.SampleCount)));
            if (rollups.Count > 0)
            {
                rawFrom = rollups.Max(r => r.HourUtc).AddHours(1);
            }
        }

        var raw = await db.MetricSamples.AsNoTracking()
            .Where(m => m.StatusSourceId == sourceId && m.TimestampUtc >= rawFrom)
            .Select(m => new { m.TimestampUtc, m.Value })
            .ToListAsync(ct);
        points.AddRange(raw.Select(m => new Bucket(m.TimestampUtc, m.Value, m.Value, m.Value, 1)));
        return points;
    }

    /// <summary>
    /// Chart series for the range, bucketed for drawing. Ranges up to 7 days read raw samples; 30 and 90 days read
    /// the hourly rollups (plus raw samples for the hours not rolled up yet). Rolled-up loss is the hourly figure,
    /// so a single bad poll inside an hour is averaged rather than shown as the worst.
    /// </summary>
    public async Task<NodeCharts> GetChartsAsync(int nodeId, ChartRange range, CancellationToken cancellationToken = default)
    {
        var to = clock.GetUtcNow().UtcDateTime;
        var from = to - Span(range);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var latency = new List<Bucket>();
        var loss = new List<Bucket>();
        var rawFrom = from;
        if (UsesRollups(range))
        {
            var rollups = await db.IcmpRollupHours.AsNoTracking()
                .Where(r => r.NodeId == nodeId && r.HourUtc >= from.AddHours(-1) && r.HourUtc < to)
                .Select(r => new { r.HourUtc, r.MinMs, r.AvgMs, r.MaxMs, r.LossPercent, r.SampleCount })
                .ToListAsync(cancellationToken);
            foreach (var r in rollups)
            {
                var t = r.HourUtc.AddMinutes(30);
                if (r.AvgMs is { } avg)
                {
                    latency.Add(new Bucket(t, r.MinMs ?? avg, avg, r.MaxMs ?? avg, r.SampleCount));
                }

                loss.Add(new Bucket(t, r.LossPercent, r.LossPercent, r.LossPercent, r.SampleCount));
            }

            if (rollups.Count > 0)
            {
                rawFrom = rollups.Max(r => r.HourUtc).AddHours(1);
            }
        }

        var icmp = await db.IcmpSamples.AsNoTracking()
            .Where(s => s.NodeId == nodeId && s.TimestampUtc >= rawFrom)
            .Select(s => new { s.TimestampUtc, s.AvgMs, s.LossPercent })
            .ToListAsync(cancellationToken);
        latency.AddRange(icmp.Where(s => s.AvgMs is not null).Select(s => new Bucket(s.TimestampUtc, s.AvgMs!.Value, s.AvgMs!.Value, s.AvgMs!.Value, 1)));
        loss.AddRange(icmp.Select(s => new Bucket(s.TimestampUtc, s.LossPercent, s.LossPercent, s.LossPercent, 1)));

        var sourceIds = await db.StatusSources.AsNoTracking()
            .Where(s => s.NodeId == nodeId && s.SourceKey == "" && (s.SourceType == StatusSourceType.Cpu || s.SourceType == StatusSourceType.Memory))
            .ToDictionaryAsync(s => s.SourceType, s => s.Id, cancellationToken);
        async Task<IReadOnlyList<Bucket>> Metric(StatusSourceType type)
        {
            if (!sourceIds.TryGetValue(type, out var id))
            {
                return [];
            }

            return Downsampler.Downsample(await MetricPointsAsync(db, id, from, to, range, cancellationToken), from, to, ChartPoints);
        }

        return new NodeCharts(
            from,
            to,
            Downsampler.Downsample(latency, from, to, ChartPoints),
            Downsampler.Downsample(loss, from, to, ChartPoints),
            await Metric(StatusSourceType.Cpu),
            await Metric(StatusSourceType.Memory));
    }
}
