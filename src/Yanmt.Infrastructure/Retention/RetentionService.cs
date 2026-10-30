using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yanmt.Core.Retention;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Settings;

namespace Yanmt.Infrastructure.Retention;

public sealed record RetentionResult(int RawSamples, int Rollups, int Events, int ConfigVersions, bool Vacuumed = false)
{
    public int Total => RawSamples + Rollups + Events + ConfigVersions;
}

/// <summary>
/// Purges data past its retention period, then runs <c>PRAGMA optimize</c> (always) and <c>VACUUM</c> (only when
/// <see cref="VacuumPolicy"/> says the free space is large). Writes one audit entry per run with the counts per type.
/// Raw samples are never purged beyond the newest rolled-up hour. The newest config version of a node is never deleted.
/// </summary>
public sealed class RetentionService(
    IDbContextFactory<YanmtDbContext> dbFactory, Func<CancellationToken, Task<RetentionSettings>> loadSettings,
    TimeProvider clock, ILogger<RetentionService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="user">Who triggered the run: an account name for a manual run, "system" for the nightly one.</param>
    public async Task<RetentionResult> RunAsync(string user = "system", CancellationToken ct = default)
    {
        // A manual run during the nightly one waits instead of doubling up the deletes and VACUUM.
        await _gate.WaitAsync(ct);
        try
        {
            return await RunCoreAsync(user, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RetentionResult> RunCoreAsync(string user, CancellationToken ct)
    {
        var settings = await loadSettings(ct);
        if (settings.Validate().Count > 0)
        {
            logger.LogWarning("Retention settings are invalid; using defaults for this run");
            settings = new RetentionSettings();
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var raw = 0;
        var newestMetric = await db.MetricRollupHours.MaxAsync(r => (DateTime?)r.HourUtc, ct);
        var newestIcmp = await db.IcmpRollupHours.MaxAsync(r => (DateTime?)r.HourUtc, ct);
        var newest = newestMetric > newestIcmp ? newestMetric : newestIcmp ?? newestMetric;
        if (settings.RawPurgeCutoff(now, newest) is { } rawCutoff)
        {
            raw += await db.MetricSamples.Where(s => s.TimestampUtc < rawCutoff).ExecuteDeleteAsync(ct);
            raw += await db.IcmpSamples.Where(s => s.TimestampUtc < rawCutoff).ExecuteDeleteAsync(ct);
        }

        var rollupCutoff = now.AddDays(-settings.RollupDays);
        var rollups = await db.MetricRollupHours.Where(r => r.HourUtc < rollupCutoff).ExecuteDeleteAsync(ct)
            + await db.IcmpRollupHours.Where(r => r.HourUtc < rollupCutoff).ExecuteDeleteAsync(ct);

        var eventCutoff = now.AddDays(-settings.EventDays);
        var events = await db.Events.Where(e => e.TimestampUtc < eventCutoff).ExecuteDeleteAsync(ct);

        var versions = settings.KeepConfigVersions is { } keep ? await PurgeConfigVersionsAsync(db, keep, ct) : 0;

        await db.Database.ExecuteSqlRawAsync("PRAGMA optimize;", ct);
        var vacuum = VacuumPolicy.ShouldVacuum(await PragmaAsync(db, "freelist_count", ct), await PragmaAsync(db, "page_count", ct));
        if (vacuum)
        {
            await db.Database.ExecuteSqlRawAsync("VACUUM;", ct);
        }

        var result = new RetentionResult(raw, rollups, events, versions, vacuum);
        logger.LogInformation(
            "Retention: purged {Raw} raw samples, {Rollups} rollups, {Events} events, {Versions} config versions; vacuumed: {Vacuumed}",
            raw, rollups, events, versions, vacuum);

        // Counts only, never row data. AuditEntry is not IAudited, so the interceptor adds nothing further.
        db.AuditEntries.Add(new Data.Entities.AuditEntry
        {
            TimestampUtc = now,
            User = user,
            Action = "RetentionCleanup",
            EntityType = "Retention",
            Changes = JsonSerializer.Serialize(new
            {
                rawSamples = raw, rollups, events, configVersions = versions, vacuumed = vacuum,
            }),
        });
        await db.SaveChangesAsync(ct);
        return result;
    }

    private static async Task<long> PragmaAsync(YanmtDbContext db, string pragma, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA {pragma};";
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        }
        finally
        {
            if (wasClosed)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<int> PurgeConfigVersionsAsync(YanmtDbContext db, int keep, CancellationToken ct)
    {
        // keep >= 1 (validated), so the newest version of every node always survives.
        var nodes = await db.ConfigVersions.Select(v => v.NodeId).Distinct().ToListAsync(ct);
        var deleted = 0;
        foreach (var nodeId in nodes)
        {
            var cutoffId = await db.ConfigVersions.Where(v => v.NodeId == nodeId)
                .OrderByDescending(v => v.CapturedUtc).ThenByDescending(v => v.Id)
                .Skip(keep).Select(v => (long?)v.Id).FirstOrDefaultAsync(ct);
            if (cutoffId is null)
            {
                continue;
            }

            // Everything at or older than the first version beyond N (by capture order, id as tiebreak).
            var boundary = await db.ConfigVersions.Where(v => v.Id == cutoffId).Select(v => v.CapturedUtc).FirstAsync(ct);
            deleted += await db.ConfigVersions
                .Where(v => v.NodeId == nodeId && (v.CapturedUtc < boundary || (v.CapturedUtc == boundary && v.Id <= cutoffId)))
                .ExecuteDeleteAsync(ct);
        }

        return deleted;
    }
}

/// <summary>Runs <see cref="RetentionService"/> once a day at about 03:00 UTC (and never at startup, so restarts don't repeat it).</summary>
public sealed class RetentionWorker(RetentionService service, TimeProvider clock, ILogger<RetentionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan RunAt = TimeSpan.FromHours(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(UntilNextRun(clock.GetUtcNow().UtcDateTime), clock, stoppingToken);
                await service.RunAsync(ct: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Nightly retention cleanup failed; will retry tomorrow");
            }
        }
    }

    public static TimeSpan UntilNextRun(DateTime nowUtc)
    {
        var next = nowUtc.Date + RunAt;
        if (next <= nowUtc)
        {
            next = next.AddDays(1);
        }

        return next - nowUtc;
    }
}
