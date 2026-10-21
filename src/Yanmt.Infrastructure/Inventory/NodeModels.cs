using System.ComponentModel.DataAnnotations;
using Yanmt.Core.Inventory;
using Yanmt.Core.Status;

namespace Yanmt.Infrastructure.Inventory;

public sealed record NodeListItem(
    int Id,
    string Hostname,
    string ManagementIp,
    string? Site,
    IReadOnlyList<string> Tags,
    NodeIconType Icon,
    bool Enabled,
    bool IcmpEnabled,
    bool SnmpEnabled,
    bool SshEnabled,
    Severity State);

/// <summary>A recorded state change, for a node's recent-events list (the full event log arrives in Phase 6).</summary>
public sealed record NodeEventItem(DateTime TimestampUtc, Data.Entities.StatusSourceType Source, Severity? OldState, Severity NewState, string Message);

/// <summary>Form model for creating or editing a node. Validation reuses <see cref="NodeRules"/>.</summary>
public sealed class NodeEditModel : IValidatableObject
{
    public string Hostname { get; set; } = "";
    public string ManagementIp { get; set; } = "";
    public string? Site { get; set; }

    /// <summary>Tags as typed: separated by ';' or ','.</summary>
    public string? Tags { get; set; }

    public NodeIconType Icon { get; set; } = NodeIconType.Router;
    public bool Enabled { get; set; } = true;
    public bool IcmpEnabled { get; set; } = true;
    public bool SnmpEnabled { get; set; }
    public bool SshEnabled { get; set; }
    public int? SnmpProfileId { get; set; }
    public int? SshProfileId { get; set; }

    /// <summary>Read-only display: the pinned SSH host key, if any. Not saved from the form.</summary>
    public string? SshHostKey { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var (error, member) in new[]
        {
            (NodeRules.ValidateHostname(Hostname), nameof(Hostname)),
            (NodeRules.ValidateManagementIp(ManagementIp), nameof(ManagementIp)),
            (NodeRules.ValidateSite(Site), nameof(Site)),
        })
        {
            if (error is not null)
            {
                yield return new ValidationResult(error, [member]);
            }
        }

        foreach (var tag in NodeRules.ParseTags(Tags))
        {
            if (NodeRules.ValidateTag(tag) is { } error)
            {
                yield return new ValidationResult(error, [nameof(Tags)]);
            }
        }
    }
}

/// <summary>Outcome of a save: success, or a message tied to a field (or null field for a general error).</summary>
public sealed record SaveResult(bool Ok, int? Id = null, string? Field = null, string? Error = null)
{
    public static SaveResult Success(int id) => new(true, id);
    public static SaveResult Fail(string? field, string error) => new(false, Field: field, Error: error);
}

public enum ImportAction
{
    Create,
    Update,
    Unchanged,
    Error,
}

public sealed record ImportPreviewRow(int Line, string? Hostname, ImportAction Action, IReadOnlyList<string> Messages);

public sealed record ImportPreview(IReadOnlyList<ImportPreviewRow> Rows, IReadOnlyList<string> FileErrors)
{
    public bool CanApply => FileErrors.Count == 0 && Rows.Count > 0 && Rows.All(r => r.Action != ImportAction.Error);
    public int Count(ImportAction action) => Rows.Count(r => r.Action == action);
}

public sealed record ImportResult(int Created, int Updated, int Unchanged);
