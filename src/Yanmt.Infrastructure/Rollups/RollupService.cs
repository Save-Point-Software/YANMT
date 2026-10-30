using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yanmt.Core.Rollups;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Data.Entities;

namespace Yanmt.Infrastructure.Rollups;

/// <summary>
/// Rolls raw samples up into hourly min/avg/max rows. Only closed hours are rolled up, and an hour is
/// rebuilt from scratch each time (delete then insert in one transaction), so running twice is harmless.
/// </summary>
public sealed class RollupService(IDbContextFactory<YanmtDbContext> dbFactory, TimeProvider clock, ILogger<RollupService> logger)
{
    /// <summary>Hours rolled per run, so a long gap catches up over several runs instead of one long transaction burst.</summary>
    public const int MaxHoursPerRun = 48;

    /// <returns>Number of hours rebuilt.</returns>
    public async Task<int> RollUpClosedHoursAsync(CancellationToken cancellationToken = default)
    {
        var currentHour = HourlyRollup.HourStart(clock.GetUtcNow().UtcDateTime);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var start = await FirstHourToRollUpAsync(db, currentHour, cancellationToken);
        if (start is null) return 0;

        var count = 0;
        for (var hour = start.Value; hour < currentHour && count < MaxHoursPerRun; hour = hour.AddHours(1), count++)
        {
            await RollUpHourAsync(db, hour, cancellationToken);
        }
        return count;
    }

    private static async Task<DateTime?> FirstHourToRollUpAsync(YanmtDbContext db, DateTime currentHour, CancellationToken ct)
    {
        // Redo the newest rolled hour (a late sample may have landed after it was rolled), then continue.
        var lastMetric = await db.MetricRollupHours.MaxAsync(r => (DateTime?)r.HourUtc, ct);
        var lastIcmp = await db.IcmpRollupHours.MaxAsync(r => (DateTime?)r.HourUtc, ct);
        var last = lastMetric > lastIcmp ? lastMetric : lastIcmp ?? lastMetric;
        if (last is not null) return last;

        var firstMetric = await db.MetricSamples.MinAsync(s => (DateTime?)s.TimestampUtc, ct);
        var firstIcmp = await db.IcmpSamples.MinAsync(s => (DateTime?)s.TimestampUtc, ct);
        var first = firstMetric is null ? firstIcmp : firstIcmp is null ? firstMetric : (firstMetric < firstIcmp ? firstMetric : firstIcmp);
        return first is null ? null : HourlyRollup.HourStart(first.Value);
    }

    private async Task RollUpHourAsync(YanmtDbContext db, DateTime hour, CancellationToken ct)
    {
        var end = hour.AddHours(1);

        var metrics = (await db.MetricSamples.AsNoTracking()
                .Where(s => s.TimestampUtc >= hour && s.TimestampUtc < end)
                .Select(s => new { s.StatusSourceId, s.Value })
                .ToListAsync(ct))
            .GroupBy(s => s.StatusSourceId)
            .Select(g => (g.Key, Roll: HourlyRollup.Metric(g.Select(x => x.Value).ToList())!))
            .ToList();

        var icmp = (await db.IcmpSamples.AsNoTracking()
                .Where(s => s.TimestampUtc >= hour && s.TimestampUtc < end)
                .Select(s => new { s.NodeId, s.Sent, s.Received, s.MinMs, s.AvgMs, s.MaxMs })
                .ToListAsync(ct))
            .GroupBy(s => s.NodeId)
            .Select(g => (g.Key, Roll: HourlyRollup.Icmp(g.Select(x => new IcmpPoint(x.Sent, x.Received, x.MinMs, x.AvgMs, x.MaxMs)).ToList())!))
            .ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.MetricRollupHours.Where(r => r.HourUtc == hour).ExecuteDeleteAsync(ct);
        await db.IcmpRollupHours.Where(r => r.HourUtc == hour).ExecuteDeleteAsync(ct);

        // A source or node deleted since the samples were read would break the foreign key; skip those.
        var sourceIds = metrics.Select(m => m.Key).ToList();
        var liveSources = (await db.StatusSources.Where(s => sourceIds.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct)).ToHashSet();
        var nodeIds = icmp.Select(m => m.Key).ToList();
        var liveNodes = (await db.Nodes.Where(n => nodeIds.Contains(n.Id)).Select(n => n.Id).ToListAsync(ct)).ToHashSet();

        foreach (var (id, r) in metrics.Where(m => liveSources.Contains(m.Key)))
        {
            db.MetricRollupHours.Add(new MetricRollupHour { StatusSourceId = id, HourUtc = hour, Min = r.Min, Avg = r.Avg, Max = r.Max, SampleCount = r.Count });
        }
        foreach (var (id, r) in icmp.Where(m => liveNodes.Contains(m.Key)))
        {
            db.IcmpRollupHours.Add(new IcmpRollupHour
            {
                NodeId = id, HourUtc = hour, Sent = r.Sent, Received = r.Received,
                MinMs = r.MinMs, AvgMs = r.AvgMs, MaxMs = r.MaxMs, LossPercent = r.LossPercent, SampleCount = r.SampleCount,
            });
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        logger.LogDebug("Rolled up hour {Hour:u}: {Metrics} metric and {Icmp} ICMP rows", hour, metrics.Count, icmp.Count);
    }
}

/// <summary>Runs <see cref="RollupService"/> shortly after startup and then every ten minutes.</summary>
public sealed class RollupWorker(RollupService service, ILogger<RollupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                // Loop until caught up; each run is capped.
                while (await service.RollUpClosedHoursAsync(stoppingToken) >= RollupService.MaxHoursPerRun) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Hourly rollup failed; will retry");
            }
        } while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
