using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yanmt.Core.Monitors;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Data.Entities;

namespace Yanmt.Infrastructure.Monitors;

internal static class MonitorMapping
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static string RulesToJson(IEnumerable<MonitorRule> rules) => JsonSerializer.Serialize(rules.ToList(), Json);

    public static List<MonitorRule> RulesFromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<MonitorRule>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return []; // a hand-edited row shouldn't take polling down; the default state applies
        }
    }

    public static MonitorSpec ToSpec(MonitorDefinition d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        OidMode = d.OidMode,
        Oid = d.Oid,
        LabelOid = d.LabelOid,
        ValueMode = d.ValueMode,
        Scale = d.Scale,
        DenominatorOid = d.DenominatorOid,
        DenominatorScale = d.DenominatorScale,
        Unit = d.Unit,
        ValueMap = ValueMap.Parse(d.ValueMap, out _),
        Rules = RulesFromJson(d.RulesJson),
        DefaultState = d.DefaultState,
        ConsecutiveMatches = d.ConsecutiveMatches,
        RaiseEvents = d.RaiseEvents,
        IntervalSeconds = d.IntervalSeconds,
        FilterOid = d.FilterOid,
        FilterOperator = d.FilterOperator,
        FilterValue = d.FilterValue,
        LabelRegex = d.LabelRegex,
    };
}

/// <summary>One editable rule row in the form.</summary>
public sealed class RuleRow
{
    public RuleOperator Operator { get; set; } = RuleOperator.Equal;
    public string Value { get; set; } = "";
    public string? Value2 { get; set; }
    public Severity State { get; set; } = Severity.Critical;
}

/// <summary>Form model for a monitor definition. OIDs may be typed as names; the service converts them.</summary>
public sealed class MonitorEditModel
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public OidMode OidMode { get; set; } = OidMode.Table;
    public string Oid { get; set; } = "";
    public string? LabelOid { get; set; }
    public ValueMode ValueMode { get; set; } = ValueMode.Raw;
    public string Scale { get; set; } = "1";
    public string? DenominatorOid { get; set; }
    public string DenominatorScale { get; set; } = "1";
    public string? Unit { get; set; }
    public string? ValueMap { get; set; }
    public List<RuleRow> Rules { get; set; } = [];
    public Severity DefaultState { get; set; } = Severity.Normal;
    public int ConsecutiveMatches { get; set; } = 1;
    public bool RaiseEvents { get; set; } = true;
    public int? IntervalSeconds { get; set; }
    public string? FilterOid { get; set; }
    public RuleOperator FilterOperator { get; set; } = RuleOperator.Equal;
    public string? FilterValue { get; set; }
    public string? LabelRegex { get; set; }
    public string? StarterKey { get; set; }

    public static MonitorEditModel From(MonitorDefinition d) => new()
    {
        Name = d.Name,
        Description = d.Description,
        Enabled = d.Enabled,
        OidMode = d.OidMode,
        Oid = d.Oid,
        LabelOid = d.LabelOid,
        ValueMode = d.ValueMode,
        Scale = d.Scale.ToString(CultureInfo.InvariantCulture),
        DenominatorOid = d.DenominatorOid,
        DenominatorScale = d.DenominatorScale.ToString(CultureInfo.InvariantCulture),
        Unit = d.Unit,
        ValueMap = d.ValueMap,
        Rules = MonitorMapping.RulesFromJson(d.RulesJson).Select(r => new RuleRow { Operator = r.Operator, Value = r.Value, Value2 = r.Value2, State = r.State }).ToList(),
        DefaultState = d.DefaultState,
        ConsecutiveMatches = d.ConsecutiveMatches,
        RaiseEvents = d.RaiseEvents,
        IntervalSeconds = d.IntervalSeconds,
        FilterOid = d.FilterOid,
        FilterOperator = d.FilterOperator ?? RuleOperator.Equal,
        FilterValue = d.FilterValue,
        LabelRegex = d.LabelRegex,
        StarterKey = d.StarterKey,
    };
}

public sealed record MonitorListItem(
    int Id,
    string Name,
    string? Description,
    bool Enabled,
    bool IsStarter,
    OidMode OidMode,
    string Oid,
    string? OidName,
    ValueMode ValueMode,
    bool IsPercent,
    int AssignmentCount,
    int InstanceCount);

public sealed record AssignmentView(int Id, AssignmentScope Scope, int? NodeId, string? Hostname, string? TagName, string? InstanceSuffix, string? InstanceLabel, int NodeCount)
{
    public string Target => Scope == AssignmentScope.Tag ? $"tag {TagName}" : Hostname ?? "?";

    public string Rows => InstanceSuffix is null ? "all rows" : InstanceLabel is { Length: > 0 } l ? $"{InstanceSuffix} ({l})" : InstanceSuffix;
}

public sealed record MonitorResult(bool Ok, string? Error = null, string? Field = null, int? Id = null)
{
    public static MonitorResult Success(int? id = null) => new(true, Id: id);

    public static MonitorResult Fail(string error, string? field = null) => new(false, error, field);
}
