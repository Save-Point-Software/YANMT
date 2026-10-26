using Microsoft.EntityFrameworkCore;
using Yanmt.Core.Monitors;
using Yanmt.Infrastructure.Collectors.Snmp;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Data.Entities;

namespace Yanmt.Infrastructure.Monitors;

/// <summary>
/// One monitor to poll on one node: either every row (through the row filters) and/or specific
/// instances, each with the label it had when picked (null when not picked, e.g. a tag assignment).
/// Scalars have the single instance "".
/// </summary>
public sealed record MonitorWork(MonitorSpec Spec, bool AllRows, IReadOnlyDictionary<string, string?> Instances);

/// <summary>Works out which monitors apply to which nodes from the definitions and assignments.</summary>
public static class MonitorPlanner
{
    /// <summary>
    /// Monitors per node. Active (what to poll): enabled definitions on enabled nodes with SNMP on
    /// and a profile. Otherwise (what may keep its instances and history): every definition on every
    /// node it's applied to, so disabling a monitor or a node doesn't throw history away. Tag
    /// assignments use the node's tags as they are now (live membership). The same monitor reached
    /// through several assignments appears once.
    /// </summary>
    public static async Task<Dictionary<int, List<MonitorWork>>> PlanAsync(YanmtDbContext db, CancellationToken cancellationToken, bool activeOnly = true)
    {
        var eligible = await db.Nodes.AsNoTracking()
            .Where(n => !activeOnly || (n.Enabled && n.SnmpEnabled && n.SnmpProfileId != null))
            .Select(n => new { n.Id, Tags = n.Tags.Select(t => t.Tag.Name).ToList() })
            .ToListAsync(cancellationToken);
        var definitions = (await db.MonitorDefinitions.AsNoTracking().Where(d => !activeOnly || d.Enabled).ToListAsync(cancellationToken))
            .ToDictionary(d => d.Id, MonitorMapping.ToSpec);
        var assignments = await db.MonitorAssignments.AsNoTracking()
            .Where(a => definitions.Keys.Contains(a.DefinitionId))
            .ToListAsync(cancellationToken);

        var byNode = eligible.ToDictionary(n => n.Id, _ => new Dictionary<int, (bool AllRows, Dictionary<string, string?> Instances)>());
        var nodesByTag = eligible
            .SelectMany(n => n.Tags.Select(t => (Tag: t, n.Id)))
            .ToLookup(x => x.Tag, x => x.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var a in assignments)
        {
            var spec = definitions[a.DefinitionId];
            var nodes = a.Scope == AssignmentScope.Node
                ? (a.NodeId is { } id && byNode.ContainsKey(id) ? [id] : [])
                : nodesByTag[a.TagName ?? ""];
            foreach (var nodeId in nodes)
            {
                var works = byNode[nodeId];
                var w = works.TryGetValue(spec.Id, out var existing) ? existing : (AllRows: false, Instances: new Dictionary<string, string?>());
                if (spec.OidMode == OidMode.Scalar)
                {
                    w.Instances.TryAdd("", null);
                }
                else if (a.InstanceSuffix is { } suffix)
                {
                    // A picked label (node assignment) wins over none (tag assignment with a fixed index).
                    if (!w.Instances.TryGetValue(suffix, out var label) || label is null)
                    {
                        w.Instances[suffix] = a.InstanceLabel;
                    }
                }
                else
                {
                    w.AllRows = true;
                }

                works[spec.Id] = w;
            }
        }

        return byNode.Where(n => n.Value.Count > 0).ToDictionary(
            n => n.Key,
            n => n.Value.Select(w => new MonitorWork(definitions[w.Key], w.Value.AllRows, w.Value.Instances)).ToList());
    }
}

/// <summary>One polled row: the value, its label, and the denominator and filter columns when the monitor has them.</summary>
public sealed record MonitorRow(string Key, RawValue Value, string? Label, RawValue? Denominator, RawValue? Filter);

public sealed record MonitorWorkReading(int DefinitionId, IReadOnlyList<MonitorRow> Rows, bool Truncated);

public sealed record MonitorReading(uint? UptimeTicks, IReadOnlyList<MonitorWorkReading> Works);

/// <summary>
/// Reads a node's due monitors in as few requests as possible: every scalar and picked instance
/// (with its label and denominator) goes into batched GETs, and every all-rows table column of
/// every monitor goes into one side-by-side GETBULK walk.
/// </summary>
public sealed class MonitorCollector(ISnmpReader snmp)
{
    public const int MaxTableRows = 2000;
    private const int VarbindsPerGet = 24;
    private const string SysUpTime = "1.3.6.1.2.1.1.3.0";

    public async Task<MonitorReading> CollectAsync(SnmpTarget target, IReadOnlyList<MonitorWork> works, CancellationToken cancellationToken)
    {
        // GETs: sysUpTime (for rate timing), then each specific instance's value, label and denominator.
        var getOids = new List<string>();
        if (works.Any(w => w.Spec.ValueMode == ValueMode.Rate))
        {
            getOids.Add(SysUpTime);
        }

        foreach (var w in works.Where(w => !w.AllRows))
        {
            foreach (var key in w.Instances.Keys)
            {
                getOids.Add(At(w.Spec.Oid, key, w.Spec.OidMode));
                if (w.Spec.LabelOid is { } label)
                {
                    getOids.Add(At(label, key, OidMode.Table));
                }

                if (w.Spec.DenominatorOid is { } den)
                {
                    getOids.Add(At(den, key, w.Spec.OidMode));
                }
            }
        }

        var got = new Dictionary<string, SnmpValue>();
        foreach (var chunk in getOids.Distinct().Chunk(VarbindsPerGet))
        {
            foreach (var v in (await snmp.GetAsync(target, chunk, cancellationToken)).Values)
            {
                got[v.Oid.TrimStart('.')] = v;
            }
        }

        // One walk for every all-rows column (value, label, denominator, filter) of every monitor.
        var columns = works.Where(w => w.AllRows)
            .SelectMany(w => new[] { w.Spec.Oid, w.Spec.LabelOid, w.Spec.DenominatorOid, w.Spec.FilterOid })
            .OfType<string>().Distinct().ToList();
        var walked = new Dictionary<string, (Dictionary<string, SnmpValue> Rows, bool Truncated)>();
        if (columns.Count > 0)
        {
            var walk = await snmp.WalkAsync(target, columns, MaxTableRows, cancellationToken);
            foreach (var c in walk.Columns)
            {
                walked[c.Root] = (c.Values.ToDictionary(v => v.Oid.TrimStart('.')[(c.Root.Length + 1)..]), c.Truncated);
            }
        }

        RawValue? Got(string oid) => got.TryGetValue(oid, out var v) ? Raw(v) : null;
        RawValue? Cell(string? column, string key) =>
            column is not null && walked.TryGetValue(column, out var c) ? c.Rows.TryGetValue(key, out var v) ? Raw(v) : RawValue.NoSuchObject : null;

        var readings = new List<MonitorWorkReading>();
        foreach (var w in works)
        {
            var s = w.Spec;
            var rows = new List<MonitorRow>();
            if (w.AllRows)
            {
                var values = walked[s.Oid];
                foreach (var (key, v) in values.Rows)
                {
                    rows.Add(new MonitorRow(key, Raw(v), Cell(s.LabelOid, key)?.Text, Cell(s.DenominatorOid, key), Cell(s.FilterOid, key)));
                }

                // Picked instances not present in the walk are reported missing, not dropped.
                foreach (var key in w.Instances.Keys.Where(k => !values.Rows.ContainsKey(k)))
                {
                    rows.Add(new MonitorRow(key, RawValue.NoSuchObject, null, null, null));
                }

                readings.Add(new MonitorWorkReading(s.Id, rows, values.Truncated));
            }
            else
            {
                foreach (var key in w.Instances.Keys)
                {
                    rows.Add(new MonitorRow(
                        key,
                        Got(At(s.Oid, key, s.OidMode)) ?? RawValue.NoSuchObject,
                        s.LabelOid is { } l ? Got(At(l, key, OidMode.Table)) is { Missing: false } lv ? lv.Text : null : null,
                        s.DenominatorOid is { } d ? Got(At(d, key, s.OidMode)) ?? RawValue.NoSuchObject : null,
                        null));
                }

                readings.Add(new MonitorWorkReading(s.Id, rows, false));
            }
        }

        uint? uptime = got.TryGetValue(SysUpTime, out var up) && up is { IsMissing: false, Number: { } ticks } ? (uint)ticks : null;
        return new MonitorReading(uptime, readings);
    }

    /// <summary>The OID to GET: a scalar as written, a table column at a row index.</summary>
    public static string At(string oid, string key, OidMode mode) => mode == OidMode.Scalar || key.Length == 0 ? oid : $"{oid}.{key}";

    public static RawValue Raw(SnmpValue v) => v.IsMissing
        ? RawValue.NoSuchObject
        : new RawValue(v.Number, v.Text, v.Type switch { "Counter32" => 32, "Counter64" => 64, _ => 0 });
}
