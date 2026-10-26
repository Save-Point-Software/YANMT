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
        _ => TimeSpan.FromDays(7),
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

    /// <summary>Last-24-hour trend per numeric source of a node (CPU, memory, custom monitors), keyed by StatusSource id.</summary>
    public async Task<(DateTime From, DateTime To, Dictionary<(StatusSourceType Type, string Key), IReadOnlyList<Bucket>> Trends)> GetTrendsAsync(
        int nodeId, CancellationToken cancellationToken = default)
    {
        var to = clock.GetUtcNow().UtcDateTime;
        var from = to.AddDays(-1);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var sources = await db.StatusSources.AsNoTracking().Where(s => s.NodeId == nodeId)
            .Select(s => new { s.Id, s.SourceType, s.SourceKey }).ToListAsync(cancellationToken);
        var ids = sources.Select(s => s.Id).ToList();
        var samples = (await db.MetricSamples.AsNoTracking()
                .Where(m => ids.Contains(m.StatusSourceId) && m.TimestampUtc >= from)
                .Select(m => new { m.StatusSourceId, m.TimestampUtc, m.Value })
                .ToListAsync(cancellationToken))
            .ToLookup(m => m.StatusSourceId);
        var trends = sources
            .Where(s => samples[s.Id].Any())
            .ToDictionary(
                s => (s.SourceType, s.SourceKey),
                s => Downsampler.Downsample(samples[s.Id].Select(m => new Sample(m.TimestampUtc, m.Value)), from, to, 60));
        return (from, to, trends);
    }

    /// <summary>
    /// Raw samples for the range, bucketed for drawing. (Hourly rollups for longer ranges arrive
    /// with retention in Phase 6; raw samples are kept 7 days, which covers every range offered.)
    /// </summary>
    public async Task<NodeCharts> GetChartsAsync(int nodeId, ChartRange range, CancellationToken cancellationToken = default)
    {
        var to = clock.GetUtcNow().UtcDateTime;
        var from = to - Span(range);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var icmp = await db.IcmpSamples.AsNoTracking()
            .Where(s => s.NodeId == nodeId && s.TimestampUtc >= from)
            .Select(s => new { s.TimestampUtc, s.AvgMs, s.LossPercent })
            .ToListAsync(cancellationToken);

        var sourceIds = await db.StatusSources.AsNoTracking()
            .Where(s => s.NodeId == nodeId && s.SourceKey == "" && (s.SourceType == StatusSourceType.Cpu || s.SourceType == StatusSourceType.Memory))
            .ToDictionaryAsync(s => s.SourceType, s => s.Id, cancellationToken);
        async Task<IReadOnlyList<Bucket>> Metric(StatusSourceType type)
        {
            if (!sourceIds.TryGetValue(type, out var id))
            {
                return [];
            }

            var samples = await db.MetricSamples.AsNoTracking()
                .Where(m => m.StatusSourceId == id && m.TimestampUtc >= from)
                .Select(m => new Sample(m.TimestampUtc, m.Value))
                .ToListAsync(cancellationToken);
            return Downsampler.Downsample(samples, from, to, ChartPoints);
        }

        return new NodeCharts(
            from,
            to,
            Downsampler.Downsample(icmp.Where(s => s.AvgMs is not null).Select(s => new Sample(s.TimestampUtc, s.AvgMs!.Value)), from, to, ChartPoints),
            Downsampler.Downsample(icmp.Select(s => new Sample(s.TimestampUtc, s.LossPercent)), from, to, ChartPoints),
            await Metric(StatusSourceType.Cpu),
            await Metric(StatusSourceType.Memory));
    }
}
