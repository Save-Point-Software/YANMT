namespace Yanmt.Infrastructure.Data.Entities;

/// <summary>Marks an entity whose admin changes are written to the audit log.</summary>
public interface IAudited;

/// <summary>Marks a property whose value must never appear in the audit log (shown as "***").</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SecretAttribute : Attribute;

public class AuditEntry
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }

    /// <summary>Windows account name (DOMAIN\user), or "system" for background work.</summary>
    public required string User { get; set; }

    /// <summary>Create, Update, or Delete.</summary>
    public required string Action { get; set; }

    public required string EntityType { get; set; }
    public string? EntityKey { get; set; }

    /// <summary>JSON: new values for Create/Delete, {"prop":{"old":..,"new":..}} for Update. Secrets are scrubbed.</summary>
    public string? Changes { get; set; }
}
