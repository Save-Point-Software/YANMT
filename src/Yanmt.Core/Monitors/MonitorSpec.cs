using Yanmt.Core.Status;

namespace Yanmt.Core.Monitors;

/// <summary>
/// What a monitor's OID is. A scalar is polled as-is (e.g. sysUpTime.0). A table column is polled
/// either at one instance (an assignment with an instance, e.g. ifOperStatus.3) or across every
/// row (an assignment without one): the spec's "column + instance" and "all rows" modes.
/// </summary>
public enum OidMode
{
    Scalar,
    Table,
}

/// <summary>Use the polled value as-is, or the per-second rate of change of a counter.</summary>
public enum ValueMode
{
    Raw,
    Rate,
}

public enum RuleOperator
{
    Equal,
    NotEqual,
    Greater,
    GreaterOrEqual,
    Less,
    LessOrEqual,
    Between,
    Contains,
    Regex,
}

/// <summary>One state rule. Rules are evaluated in order; the first match wins.</summary>
public sealed record MonitorRule(RuleOperator Operator, string Value, string? Value2, Severity State);

/// <summary>
/// Everything needed to poll and evaluate a monitor, as a pure value (the DB entity maps to this).
/// For table monitors, LabelOid, DenominatorOid and FilterOid are columns of the same table and
/// are read at the same row index.
/// </summary>
public sealed record MonitorSpec
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public OidMode OidMode { get; init; }

    /// <summary>Numeric OID: the scalar itself, or the table column (without an index).</summary>
    public string Oid { get; init; } = "";

    /// <summary>Column giving each row a readable label (e.g. ifDescr), used by the picker, table rows and the instance check.</summary>
    public string? LabelOid { get; init; }

    public ValueMode ValueMode { get; init; }

    /// <summary>Multiplies the raw value or rate (e.g. 8 to turn octets into bits).</summary>
    public decimal Scale { get; init; } = 1;

    /// <summary>When set, the value is a percentage of this OID (times <see cref="DenominatorScale"/>).</summary>
    public string? DenominatorOid { get; init; }

    public decimal DenominatorScale { get; init; } = 1;

    public string? Unit { get; init; }
    public ValueMap ValueMap { get; init; } = ValueMap.Empty;
    public IReadOnlyList<MonitorRule> Rules { get; init; } = [];
    public Severity DefaultState { get; init; } = Severity.Normal;

    /// <summary>Consecutive polls a new state must hold before it takes effect (1 = no dampening).</summary>
    public int ConsecutiveMatches { get; init; } = 1;

    public bool RaiseEvents { get; init; } = true;

    /// <summary>Null polls at the global SNMP interval.</summary>
    public int? IntervalSeconds { get; init; }

    /// <summary>All-rows table assignments only: keep rows where FilterOid's value passes this test.</summary>
    public string? FilterOid { get; init; }

    public RuleOperator? FilterOperator { get; init; }
    public string? FilterValue { get; init; }

    /// <summary>All-rows table assignments only: keep rows whose label matches this regex.</summary>
    public string? LabelRegex { get; init; }

    public bool IsPercent => DenominatorOid is not null;

    public bool HasRowFilter => FilterOid is not null || LabelRegex is not null;
}
