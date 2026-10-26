using Yanmt.Core.Monitors;
using Yanmt.Core.Status;

namespace Yanmt.Infrastructure.Data.Entities;

/// <summary>
/// A user-defined monitor: what to poll and how to turn the value into a state. It is also the
/// template: assignments apply it to nodes, node instances, or tags, and editing it changes every
/// place it's applied.
/// </summary>
public class MonitorDefinition : IAudited
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; }

    /// <summary>Set on the shipped examples (starter library). Purely informational; they edit like any other.</summary>
    public string? StarterKey { get; set; }

    public OidMode OidMode { get; set; }
    public required string Oid { get; set; }
    public string? LabelOid { get; set; }
    public ValueMode ValueMode { get; set; }
    public decimal Scale { get; set; } = 1;
    public string? DenominatorOid { get; set; }
    public decimal DenominatorScale { get; set; } = 1;
    public string? Unit { get; set; }

    /// <summary>"1=up, 2=down" text form.</summary>
    public string? ValueMap { get; set; }

    /// <summary>Ordered rules as JSON (a list of MonitorRule).</summary>
    public string RulesJson { get; set; } = "[]";

    public Severity DefaultState { get; set; } = Severity.Normal;
    public int ConsecutiveMatches { get; set; } = 1;
    public bool RaiseEvents { get; set; } = true;
    public int? IntervalSeconds { get; set; }

    public string? FilterOid { get; set; }
    public RuleOperator? FilterOperator { get; set; }
    public string? FilterValue { get; set; }
    public string? LabelRegex { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public List<MonitorAssignment> Assignments { get; set; } = [];
}

public enum AssignmentScope
{
    Node,
    Tag,
}

/// <summary>
/// Applies a monitor to one node, or to every node with a tag (live: membership is read on each
/// reload). For table monitors, an instance suffix polls one row; no suffix polls all rows
/// (through the monitor's row filters). The tag is stored by name, so it survives the tag being
/// pruned while no node carries it.
/// </summary>
public class MonitorAssignment : IAudited
{
    public int Id { get; set; }
    public int DefinitionId { get; set; }
    public MonitorDefinition Definition { get; set; } = null!;
    public AssignmentScope Scope { get; set; }
    public int? NodeId { get; set; }
    public Node? Node { get; set; }
    public string? TagName { get; set; }

    /// <summary>Table monitors: the row index (e.g. "3" or "192.0.2.1"). Null polls every row.</summary>
    public string? InstanceSuffix { get; set; }

    /// <summary>The row's label when it was picked; a different label at poll time means the index moved.</summary>
    public string? InstanceLabel { get; set; }

    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// One polled thing a monitor produces on one node: the scalar, a picked row, or one row of an
/// all-rows table. Created and removed by the status engine (not audited); its state lives in a
/// StatusSource of type Custom keyed by this id.
/// </summary>
public class MonitorInstance
{
    public int Id { get; set; }
    public int DefinitionId { get; set; }
    public MonitorDefinition Definition { get; set; } = null!;
    public int NodeId { get; set; }
    public Node Node { get; set; } = null!;

    /// <summary>"" for a scalar, otherwise the row index.</summary>
    public string InstanceKey { get; set; } = "";

    public string? Label { get; set; }

    /// <summary>Rate baseline: the previous raw counter, when it was read, and the agent's sysUpTime then.</summary>
    public decimal? LastRaw { get; set; }

    public DateTime? LastRawUtc { get; set; }
    public long? LastUptimeTicks { get; set; }
}
