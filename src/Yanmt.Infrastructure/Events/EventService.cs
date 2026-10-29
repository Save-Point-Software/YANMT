using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Data.Entities;
using Yanmt.Infrastructure.Security;

namespace Yanmt.Infrastructure.Events;

/// <summary>Event log filter. Null means "any".</summary>
public sealed record EventFilter(int? NodeId = null, Severity? State = null, StatusSourceType? Source = null, bool UnacknowledgedOnly = false);

public sealed record EventRow(
    long Id, DateTime TimestampUtc, int? NodeId, string? Hostname, StatusSourceType Source,
    Severity? OldState, Severity NewState, string Message, string? AcknowledgedBy, DateTime? AcknowledgedUtc);

/// <summary>Reads the event log and acknowledges events. Acknowledgement is audited.</summary>
public sealed class EventService(
    IDbContextFactory<YanmtDbContext> dbFactory,
    ICurrentUser currentUser,
    AuditLogService audit,
    TimeProvider clock,
    InAppNotifier notifier)
{
    private static IQueryable<Event> Apply(IQueryable<Event> q, EventFilter f)
    {
        if (f.NodeId is { } node) { q = q.Where(e => e.NodeId == node); }
        if (f.State is { } state) { q = q.Where(e => e.NewState == state); }
        if (f.Source is { } source) { q = q.Where(e => e.Source == source); }
        if (f.UnacknowledgedOnly) { q = q.Where(e => e.AcknowledgedUtc == null); }
        return q;
    }

    public async Task<(List<EventRow> Rows, int Total)> QueryAsync(EventFilter filter, int skip, int take, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = Apply(db.Events.AsNoTracking(), filter);
        var total = await q.CountAsync(cancellationToken);
        var rows = await q
            .OrderByDescending(e => e.TimestampUtc).ThenByDescending(e => e.Id)
            .Skip(skip).Take(take)
            .Select(e => new EventRow(e.Id, e.TimestampUtc, e.NodeId, e.Node != null ? e.Node.Hostname : null, e.Source,
                e.OldState, e.NewState, e.Message, e.AcknowledgedBy, e.AcknowledgedUtc))
            .ToListAsync(cancellationToken);
        return (rows, total);
    }

    /// <summary>Unacknowledged Critical events across all nodes (the layout badge).</summary>
    public async Task<int> CountUnacknowledgedCriticalAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Events.AsNoTracking()
            .CountAsync(e => e.NewState == Severity.Critical && e.AcknowledgedUtc == null, cancellationToken);
    }

    /// <summary>Every event matching <paramref name="filter"/>, newest first, as CSV (no secrets; same columns for any filter).</summary>
    public async Task<string> ExportCsvAsync(EventFilter filter, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await Apply(db.Events.AsNoTracking(), filter)
            .OrderByDescending(e => e.TimestampUtc).ThenByDescending(e => e.Id)
            .Select(e => new { e.Id, e.TimestampUtc, Hostname = e.Node != null ? e.Node.Hostname : null, e.Source, e.OldState, e.NewState, e.Message, e.AcknowledgedBy, e.AcknowledgedUtc })
            .ToListAsync(cancellationToken);
        return EventCsv.Export(rows.Select(e => new EventCsvRecord(
            e.Id, e.TimestampUtc, e.Hostname, e.Source.ToString(), e.OldState, e.NewState, e.Message, e.AcknowledgedBy, e.AcknowledgedUtc)));
    }

    /// <summary>Counts the unacknowledged events matching <paramref name="filter"/> (what acknowledge-all would change).</summary>
    public async Task<int> CountUnacknowledgedAsync(EventFilter filter, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await Apply(db.Events.AsNoTracking(), filter with { UnacknowledgedOnly = true }).CountAsync(cancellationToken);
    }

    /// <summary>
    /// Acknowledges one event. Admin only; throws <see cref="UnauthorizedAccessException"/> otherwise.
    /// Returns false if it doesn't exist or was already acknowledged.
    /// </summary>
    public async Task<bool> AcknowledgeAsync(long id, ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        if (!principal.IsInRole(YanmtRoles.Admin))
        {
            throw new UnauthorizedAccessException("Only admins can acknowledge events.");
        }

        var user = await currentUser.GetNameAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var changed = await db.Events.Where(e => e.Id == id && e.AcknowledgedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.AcknowledgedBy, user).SetProperty(e => e.AcknowledgedUtc, now), cancellationToken);
        if (changed == 0)
        {
            return false;
        }

        await audit.RecordAsync("AcknowledgeEvent", "Event", id.ToString(), null, cancellationToken);
        notifier.RaiseAcknowledged();
        return true;
    }

    /// <summary>
    /// Acknowledges every unacknowledged event matching <paramref name="filter"/> (the filter as
    /// shown on the page). Admin only; throws <see cref="UnauthorizedAccessException"/> otherwise.
    /// Returns how many events were acknowledged. Audited with the filter and count.
    /// </summary>
    public async Task<int> AcknowledgeAllAsync(EventFilter filter, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        if (!user.IsInRole(YanmtRoles.Admin))
        {
            throw new UnauthorizedAccessException("Only admins can acknowledge all events.");
        }

        var name = await currentUser.GetNameAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var count = await Apply(db.Events, filter with { UnacknowledgedOnly = true })
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.AcknowledgedBy, name).SetProperty(e => e.AcknowledgedUtc, now), cancellationToken);
        await audit.RecordAsync("AcknowledgeAllEvents", "Event", null,
            JsonSerializer.Serialize(new { nodeId = filter.NodeId, state = filter.State?.ToString(), source = filter.Source?.ToString(), count }), cancellationToken);
        notifier.RaiseAcknowledged();
        return count;
    }
}
