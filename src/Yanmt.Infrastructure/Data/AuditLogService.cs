using Microsoft.EntityFrameworkCore;
using Yanmt.Infrastructure.Data.Entities;

namespace Yanmt.Infrastructure.Data;

/// <summary>Read side of the audit log (writes happen only through <see cref="AuditInterceptor"/>).</summary>
public sealed class AuditLogService(IDbContextFactory<YanmtDbContext> dbFactory, ICurrentUser currentUser, TimeProvider clock)
{
    /// <summary>
    /// Records an action that isn't an entity change (e.g. revealing a secret config). Never put secret values in <paramref name="changes"/>.
    /// </summary>
    public async Task RecordAsync(string action, string entityType, string? entityKey, string? changes, CancellationToken cancellationToken = default)
    {
        var user = await currentUser.GetNameAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.AuditEntries.Add(new AuditEntry
        {
            TimestampUtc = clock.GetUtcNow().UtcDateTime,
            User = user,
            Action = action,
            EntityType = entityType,
            EntityKey = entityKey,
            Changes = changes,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<AuditEntry>> RecentAsync(string? entityType = null, string? user = null, int take = 200, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.AuditEntries.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(entityType))
        {
            query = query.Where(a => a.EntityType == entityType);
        }

        if (!string.IsNullOrWhiteSpace(user))
        {
            query = query.Where(a => a.User.Contains(user));
        }

        return await query.OrderByDescending(a => a.Id).Take(take).ToListAsync(cancellationToken);
    }

    public async Task<List<string>> EntityTypesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.AuditEntries.AsNoTracking().Select(a => a.EntityType).Distinct().OrderBy(t => t).ToListAsync(cancellationToken);
    }
}
