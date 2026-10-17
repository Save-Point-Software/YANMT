using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Yanmt.Infrastructure.Data.Entities;

namespace Yanmt.Infrastructure.Data;

/// <summary>
/// Writes an <see cref="AuditEntry"/> for every create/update/delete of an <see cref="IAudited"/> entity.
/// Values of properties marked <see cref="SecretAttribute"/> are replaced with "***".
/// Only SaveChangesAsync is supported for audited changes: resolving the Blazor user is async,
/// and blocking on it from a circuit would risk a deadlock.
/// </summary>
public sealed class AuditInterceptor(ICurrentUser currentUser, TimeProvider clock) : SaveChangesInterceptor
{
    internal const string SecretMask = "***";

    internal sealed record PendingAudit(EntityEntry Entry, string Action, string? Changes);

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is YanmtDbContext db && db.ChangeTracker.Entries<IAudited>().Any(IsAuditable))
        {
            throw new InvalidOperationException("Audited entities must be saved with SaveChangesAsync.");
        }

        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is YanmtDbContext db)
        {
            db.PendingAudits.Clear();
            foreach (var entry in db.ChangeTracker.Entries<IAudited>().Where(IsAuditable).ToList())
            {
                var action = entry.State switch
                {
                    EntityState.Added => "Create",
                    EntityState.Modified => "Update",
                    _ => "Delete",
                };

                // Added rows may not have their generated key yet, so their values are captured after the save.
                var changes = entry.State == EntityState.Added ? null : DescribeChanges(entry);
                if (entry.State == EntityState.Modified && changes is null)
                {
                    continue; // only timestamps changed
                }

                db.PendingAudits.Add(new PendingAudit(entry, action, changes));
            }
        }

        return ValueTask.FromResult(result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not YanmtDbContext db || db.PendingAudits.Count == 0)
        {
            return result;
        }

        var user = await currentUser.GetNameAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var pending = db.PendingAudits.ToList();
        db.PendingAudits.Clear();

        foreach (var p in pending)
        {
            db.AuditEntries.Add(new AuditEntry
            {
                TimestampUtc = now,
                User = user,
                Action = p.Action,
                EntityType = p.Entry.Metadata.ClrType.Name,
                EntityKey = KeyOf(p.Entry),
                Changes = p.Changes ?? DescribeValues(p.Entry, current: true),
            });
        }

        // AuditEntry is not IAudited, so this nested save records nothing further.
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is YanmtDbContext db)
        {
            db.PendingAudits.Clear();
        }

        return Task.CompletedTask;
    }

    private static bool IsAuditable(EntityEntry entry) =>
        entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted;

    private static string? DescribeChanges(EntityEntry entry) => entry.State switch
    {
        EntityState.Deleted => DescribeValues(entry, current: false),
        EntityState.Modified => DescribeModified(entry),
        _ => null,
    };

    private static string DescribeValues(EntityEntry entry, bool current)
    {
        var values = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in entry.Properties.Where(p => !p.Metadata.IsPrimaryKey()))
        {
            values[prop.Metadata.Name] = Present(prop, current ? prop.CurrentValue : prop.OriginalValue);
        }

        return JsonSerializer.Serialize(values);
    }

    private static string? DescribeModified(EntityEntry entry)
    {
        var values = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in entry.Properties.Where(p => p.IsModified && !p.Metadata.IsPrimaryKey()))
        {
            if (Equals(prop.OriginalValue, prop.CurrentValue) || prop.Metadata.Name.EndsWith("Utc", StringComparison.Ordinal))
            {
                continue;
            }

            values[prop.Metadata.Name] = new { old = Present(prop, prop.OriginalValue), @new = Present(prop, prop.CurrentValue) };
        }

        return values.Count == 0 ? null : JsonSerializer.Serialize(values);
    }

    private static object? Present(PropertyEntry prop, object? value)
    {
        if (prop.Metadata.PropertyInfo?.GetCustomAttribute<SecretAttribute>() is not null)
        {
            return value is null ? null : SecretMask;
        }

        return value switch
        {
            byte[] bytes => $"<{bytes.Length} bytes>",
            Enum e => e.ToString(),
            _ => value,
        };
    }

    private static string? KeyOf(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        return key is null ? null : string.Join(",", key.Properties.Select(p => entry.Property(p.Name).CurrentValue));
    }
}
