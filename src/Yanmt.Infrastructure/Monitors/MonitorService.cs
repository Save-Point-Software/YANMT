using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yanmt.Core.Inventory;
using Yanmt.Core.Monitors;
using Yanmt.Core.Oids;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Data.Entities;
using Yanmt.Infrastructure.Monitoring;

namespace Yanmt.Infrastructure.Monitors;

/// <summary>Monitor definitions and their assignments. Writes are audited and wake the pollers.</summary>
public sealed class MonitorService(IDbContextFactory<YanmtDbContext> dbFactory, IOidNameResolver names, TimeProvider clock, InventoryChangeSignal? signal = null)
{
    public const string IfTable = "1.3.6.1.2.1.2.2.1";
    public const string IfXTable = "1.3.6.1.2.1.31.1.1.1";

    public async Task<List<MonitorListItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var instances = await db.MonitorInstances.AsNoTracking().GroupBy(i => i.DefinitionId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var defs = await db.MonitorDefinitions.AsNoTracking().OrderBy(d => d.Name)
            .Select(d => new { d.Id, d.Name, d.Description, d.Enabled, d.StarterKey, d.OidMode, d.Oid, d.ValueMode, d.DenominatorOid, Assignments = d.Assignments.Count })
            .ToListAsync(cancellationToken);
        return defs.Select(d => new MonitorListItem(d.Id, d.Name, d.Description, d.Enabled, d.StarterKey is not null, d.OidMode, d.Oid,
                names.Describe(d.Oid), d.ValueMode, d.DenominatorOid is not null, d.Assignments, instances.GetValueOrDefault(d.Id)))
            .ToList();
    }

    public async Task<(MonitorEditModel Model, List<AssignmentView> Assignments)?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var d = await db.MonitorDefinitions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        return d is null ? null : (MonitorEditModel.From(d), await AssignmentsAsync(db, id, cancellationToken));
    }

    public async Task<MonitorSpec?> GetSpecAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var d = await db.MonitorDefinitions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        return d is null ? null : MonitorMapping.ToSpec(d);
    }

    private static async Task<List<AssignmentView>> AssignmentsAsync(YanmtDbContext db, int definitionId, CancellationToken cancellationToken)
    {
        var rows = await db.MonitorAssignments.AsNoTracking().Where(a => a.DefinitionId == definitionId)
            .OrderBy(a => a.Scope).ThenBy(a => a.TagName).ThenBy(a => a.Node!.Hostname).ThenBy(a => a.InstanceSuffix)
            .Select(a => new { a.Id, a.Scope, a.NodeId, Hostname = a.Node != null ? a.Node.Hostname : null, a.TagName, a.InstanceSuffix, a.InstanceLabel })
            .ToListAsync(cancellationToken);
        var tagCounts = await db.Tags.AsNoTracking().Select(t => new { t.Name, Count = t.Nodes.Count }).ToListAsync(cancellationToken);
        return rows.Select(a => new AssignmentView(a.Id, a.Scope, a.NodeId, a.Hostname, a.TagName, a.InstanceSuffix, a.InstanceLabel,
                a.Scope == AssignmentScope.Node ? 1 : tagCounts.FirstOrDefault(t => string.Equals(t.Name, a.TagName, StringComparison.OrdinalIgnoreCase))?.Count ?? 0))
            .ToList();
    }

    /// <summary>
    /// Turns the form into a spec: names become numeric OIDs, scales and the value map are parsed,
    /// and everything is validated. Returns the spec and the first error per field.
    /// </summary>
    public (MonitorSpec Spec, IReadOnlyList<(string Field, string Error)> Errors) BuildSpec(MonitorEditModel m, int id = 0)
    {
        var errors = new List<(string, string)>();
        string? Oid(string? text, string field, bool required)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                if (required)
                {
                    errors.Add((field, "Required."));
                }

                return null;
            }

            if (!names.TryParse(text, out var oid))
            {
                errors.Add((field, $"'{text.Trim()}' isn't a numeric OID or a known name."));
                return null;
            }

            return oid;
        }

        decimal Scale(string text, string field)
        {
            if (decimal.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                return v;
            }

            errors.Add((field, "Must be a number, e.g. 8 or 1000000."));
            return 1;
        }

        var map = ValueMap.Parse(m.ValueMap, out var mapError);
        if (mapError is not null)
        {
            errors.Add((nameof(m.ValueMap), mapError));
        }

        var table = m.OidMode == OidMode.Table;
        var filterOid = table ? Oid(m.FilterOid, nameof(m.FilterOid), false) : null;
        var spec = new MonitorSpec
        {
            Id = id,
            Name = m.Name.Trim(),
            OidMode = m.OidMode,
            Oid = Oid(m.Oid, nameof(m.Oid), true) ?? "",
            LabelOid = table ? Oid(m.LabelOid, nameof(m.LabelOid), false) : null,
            ValueMode = m.ValueMode,
            Scale = Scale(m.Scale, nameof(m.Scale)),
            DenominatorOid = Oid(m.DenominatorOid, nameof(m.DenominatorOid), false),
            DenominatorScale = Scale(m.DenominatorScale, nameof(m.DenominatorScale)),
            Unit = string.IsNullOrWhiteSpace(m.Unit) ? null : m.Unit.Trim(),
            ValueMap = map,
            Rules = m.Rules.Select(r => new MonitorRule(r.Operator, r.Value?.Trim() ?? "", r.Operator == RuleOperator.Between ? r.Value2?.Trim() : null, r.State)).ToList(),
            DefaultState = m.DefaultState,
            ConsecutiveMatches = m.ConsecutiveMatches,
            RaiseEvents = m.RaiseEvents,
            IntervalSeconds = m.IntervalSeconds,
            FilterOid = filterOid,
            FilterOperator = filterOid is null ? null : m.FilterOperator,
            FilterValue = filterOid is null ? null : m.FilterValue?.Trim(),
            LabelRegex = table && !string.IsNullOrWhiteSpace(m.LabelRegex) ? m.LabelRegex.Trim() : null,
        };

        errors.AddRange(MonitorValidation.Validate(spec, m.Description));
        var first = errors.GroupBy(e => e.Item1).Select(g => g.First()).ToList();
        return (spec, first);
    }

    public Task<MonitorResult> CreateAsync(MonitorEditModel model, CancellationToken cancellationToken = default) => SaveAsync(null, model, cancellationToken);

    public Task<MonitorResult> UpdateAsync(int id, MonitorEditModel model, CancellationToken cancellationToken = default) => SaveAsync(id, model, cancellationToken);

    private async Task<MonitorResult> SaveAsync(int? id, MonitorEditModel model, CancellationToken cancellationToken)
    {
        var (spec, errors) = BuildSpec(model, id ?? 0);
        if (errors.Count > 0)
        {
            return MonitorResult.Fail(errors[0].Error, errors[0].Field);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.MonitorDefinitions.AnyAsync(d => d.Name == spec.Name && d.Id != id, cancellationToken))
        {
            return MonitorResult.Fail($"A monitor named '{spec.Name}' already exists.", nameof(model.Name));
        }

        var now = clock.GetUtcNow().UtcDateTime;
        MonitorDefinition? d;
        if (id is null)
        {
            d = new MonitorDefinition { Name = spec.Name, Oid = spec.Oid, CreatedUtc = now };
            db.MonitorDefinitions.Add(d);
        }
        else
        {
            d = await db.MonitorDefinitions.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (d is null)
            {
                return MonitorResult.Fail("The monitor no longer exists.");
            }

            if (d.OidMode != spec.OidMode && await db.MonitorAssignments.AnyAsync(a => a.DefinitionId == d.Id && a.InstanceSuffix != null, cancellationToken))
            {
                return MonitorResult.Fail("Remove the per-instance assignments before changing between scalar and table.", nameof(model.OidMode));
            }
        }

        Apply(d, spec, model);
        d.UpdatedUtc = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            return MonitorResult.Fail($"A monitor named '{spec.Name}' already exists.", nameof(model.Name));
        }

        // A changed OID or label column invalidates rate baselines and row labels; the engine rebuilds them.
        if (id is not null)
        {
            await db.MonitorInstances.Where(i => i.DefinitionId == d.Id).ExecuteUpdateAsync(
                s => s.SetProperty(i => i.LastRaw, (decimal?)null).SetProperty(i => i.LastRawUtc, (DateTime?)null).SetProperty(i => i.LastUptimeTicks, (long?)null),
                cancellationToken);
        }

        signal?.Notify();
        return MonitorResult.Success(d.Id);
    }

    private static void Apply(MonitorDefinition d, MonitorSpec spec, MonitorEditModel model)
    {
        d.Name = spec.Name;
        d.Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim();
        d.Enabled = model.Enabled;
        d.OidMode = spec.OidMode;
        d.Oid = spec.Oid;
        d.LabelOid = spec.LabelOid;
        d.ValueMode = spec.ValueMode;
        d.Scale = spec.Scale;
        d.DenominatorOid = spec.DenominatorOid;
        d.DenominatorScale = spec.DenominatorScale;
        d.Unit = spec.Unit;
        d.ValueMap = spec.ValueMap.IsEmpty ? null : spec.ValueMap.ToText();
        d.RulesJson = MonitorMapping.RulesToJson(spec.Rules);
        d.DefaultState = spec.DefaultState;
        d.ConsecutiveMatches = spec.ConsecutiveMatches;
        d.RaiseEvents = spec.RaiseEvents;
        d.IntervalSeconds = spec.IntervalSeconds;
        d.FilterOid = spec.FilterOid;
        d.FilterOperator = spec.FilterOperator;
        d.FilterValue = spec.FilterValue;
        d.LabelRegex = spec.LabelRegex;
    }

    /// <summary>An independent duplicate (disabled, without assignments) to adapt, e.g. from the starter library.</summary>
    public async Task<MonitorResult> CopyAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var d = await db.MonitorDefinitions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (d is null)
        {
            return MonitorResult.Fail("The monitor no longer exists.");
        }

        var existing = await db.MonitorDefinitions.Select(x => x.Name).ToListAsync(cancellationToken);
        var name = $"Copy of {d.Name}";
        for (var n = 2; existing.Contains(name, StringComparer.OrdinalIgnoreCase); n++)
        {
            name = $"Copy {n} of {d.Name}";
        }

        var model = MonitorEditModel.From(d);
        model.Name = name.Length > MonitorValidation.MaxNameLength ? name[..MonitorValidation.MaxNameLength] : name;
        model.Enabled = false;
        model.StarterKey = null;
        return await CreateAsync(model, cancellationToken);
    }

    public async Task<MonitorResult> SetEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var d = await db.MonitorDefinitions.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (d is null)
        {
            return MonitorResult.Fail("The monitor no longer exists.");
        }

        d.Enabled = enabled;
        d.UpdatedUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        signal?.Notify();
        return MonitorResult.Success(id);
    }

    /// <summary>Deletes a monitor, its assignments, and (via the engine's next sync) its states and history.</summary>
    public async Task<MonitorResult> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var d = await db.MonitorDefinitions.Include(x => x.Assignments).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (d is null)
        {
            return MonitorResult.Fail("The monitor no longer exists.");
        }

        db.MonitorAssignments.RemoveRange(d.Assignments); // removed explicitly so each is audited
        db.MonitorDefinitions.Remove(d);
        await db.SaveChangesAsync(cancellationToken);
        signal?.Notify();
        return MonitorResult.Success();
    }

    /// <summary>
    /// Applies a monitor. Node scope may carry an instance (table monitors only); tag scope is live
    /// membership. An exact duplicate of an existing assignment is refused.
    /// </summary>
    public async Task<MonitorResult> AddAssignmentAsync(int definitionId, AssignmentScope scope, int? nodeId, string? tagName,
        string? instanceSuffix, string? instanceLabel, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var d = await db.MonitorDefinitions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == definitionId, cancellationToken);
        if (d is null)
        {
            return MonitorResult.Fail("The monitor no longer exists.");
        }

        instanceSuffix = string.IsNullOrWhiteSpace(instanceSuffix) ? null : instanceSuffix.Trim().TrimStart('.');
        if (instanceSuffix is not null && (d.OidMode != Core.Monitors.OidMode.Table || !MonitorValidation.IsInstanceSuffix(instanceSuffix)))
        {
            return MonitorResult.Fail(d.OidMode == Core.Monitors.OidMode.Table
                ? $"'{instanceSuffix}' isn't a valid instance (numbers separated by dots, e.g. 3 or 192.0.2.1)."
                : "A scalar monitor has no instances.");
        }

        if (scope == AssignmentScope.Node)
        {
            if (nodeId is null || !await db.Nodes.AnyAsync(n => n.Id == nodeId, cancellationToken))
            {
                return MonitorResult.Fail("Choose a node.");
            }

            tagName = null;
        }
        else
        {
            tagName = tagName?.Trim();
            if (string.IsNullOrEmpty(tagName) || NodeRules.ValidateTag(tagName) is not null)
            {
                return MonitorResult.Fail("Choose a tag.");
            }

            nodeId = null;
        }

        if (await db.MonitorAssignments.AnyAsync(a => a.DefinitionId == definitionId && a.Scope == scope && a.NodeId == nodeId
                && a.TagName == tagName && a.InstanceSuffix == instanceSuffix, cancellationToken))
        {
            return MonitorResult.Fail("It's already applied there.");
        }

        var assignment = new MonitorAssignment
        {
            DefinitionId = definitionId,
            Scope = scope,
            NodeId = nodeId,
            TagName = tagName,
            InstanceSuffix = instanceSuffix,
            InstanceLabel = instanceSuffix is null ? null : Clip(instanceLabel, 255),
            CreatedUtc = clock.GetUtcNow().UtcDateTime,
        };
        db.MonitorAssignments.Add(assignment);
        await db.SaveChangesAsync(cancellationToken);
        signal?.Notify();
        return MonitorResult.Success(assignment.Id);
    }

    public async Task<MonitorResult> RemoveAssignmentAsync(int assignmentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var a = await db.MonitorAssignments.FirstOrDefaultAsync(x => x.Id == assignmentId, cancellationToken);
        if (a is null)
        {
            return MonitorResult.Fail("That assignment no longer exists.");
        }

        db.MonitorAssignments.Remove(a);
        await db.SaveChangesAsync(cancellationToken);
        signal?.Notify();
        return MonitorResult.Success();
    }

    public async Task<List<string>> TagNamesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Tags.AsNoTracking().OrderBy(t => t.Name).Select(t => t.Name).ToListAsync(cancellationToken);
    }

    /// <summary>Enabled table monitors on an IF-MIB column: the choices for "Monitor this" on the interfaces tab.</summary>
    public async Task<List<(int Id, string Name)>> InterfaceMonitorsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var defs = await db.MonitorDefinitions.AsNoTracking()
            .Where(d => d.OidMode == Core.Monitors.OidMode.Table)
            .OrderBy(d => d.Name)
            .Select(d => new { d.Id, d.Name, d.Oid, d.Enabled })
            .ToListAsync(cancellationToken);
        return defs.Where(d => d.Enabled && (d.Oid.StartsWith(IfTable + ".", StringComparison.Ordinal) || d.Oid.StartsWith(IfXTable + ".", StringComparison.Ordinal)))
            .Select(d => (d.Id, d.Name)).ToList();
    }

    private static string? Clip(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
