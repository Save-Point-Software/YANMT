using Yanmt.Core.Monitors;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Collectors.Snmp;
using Yanmt.Infrastructure.Data.Entities;
using Yanmt.Infrastructure.Monitoring;

namespace Yanmt.Infrastructure.Monitors;

public sealed record MonitorTestRow(string Key, string? Label, string Value, Severity State, string Explanation, bool FilteredOut);

public sealed record MonitorTestResult(IReadOnlyList<MonitorTestRow> Rows, int TotalRows, bool SampledTwice, string? Error)
{
    public static MonitorTestResult Fail(string error) => new([], 0, false, error);
}

public sealed record InstanceChoice(string Suffix, string? Label);

/// <summary>
/// On-demand, read-only probing for the monitor editor: "Test on node" (reads the OID now and runs
/// the rules, without saving anything), the instance picker (walks the label column), and
/// "Monitor this" from the interfaces tab.
/// </summary>
public sealed class MonitorProbeService(SnmpTargetResolver targets, MonitorCollector collector, ISnmpReader snmp, MonitorService monitors, TimeProvider clock)
{
    public const int MaxTestRows = 50;
    public static readonly TimeSpan RateGap = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Reads the monitor on a node and evaluates it like the poller would (a rate takes two samples
    /// <paramref name="rateGap"/> apart). Without an instance, a table monitor reads every row and
    /// marks the rows its filters would skip.
    /// </summary>
    public async Task<MonitorTestResult> TestAsync(MonitorSpec spec, int nodeId, string? instance, TimeSpan rateGap, CancellationToken cancellationToken = default)
    {
        var resolved = await targets.ResolveAsync(nodeId, cancellationToken);
        if (resolved.Target is not { } target)
        {
            return MonitorTestResult.Fail(resolved.Error!);
        }

        instance = string.IsNullOrWhiteSpace(instance) ? null : instance.Trim().TrimStart('.');
        if (instance is not null && !MonitorValidation.IsInstanceSuffix(instance))
        {
            return MonitorTestResult.Fail($"'{instance}' isn't a valid instance.");
        }

        var allRows = spec.OidMode == OidMode.Table && instance is null;
        var work = new MonitorWork(spec, allRows,
            new Dictionary<string, string?> { [spec.OidMode == OidMode.Scalar ? "" : instance ?? ""] = null }.Where(_ => !allRows).ToDictionary());
        try
        {
            var first = await collector.CollectAsync(target, [work], cancellationToken);
            var firstAt = clock.GetUtcNow().UtcDateTime;
            var reading = first;
            var at = firstAt;
            var twice = spec.ValueMode == ValueMode.Rate;
            if (twice)
            {
                await Task.Delay(rateGap, clock, cancellationToken);
                reading = await collector.CollectAsync(target, [work], cancellationToken);
                at = clock.GetUtcNow().UtcDateTime;
            }

            var before = first.Works[0].Rows.ToDictionary(r => r.Key);
            var rows = reading.Works[0].Rows;
            var result = new List<MonitorTestRow>();
            foreach (var row in rows.Take(MaxTestRows))
            {
                RateBaseline? baseline = twice && before.TryGetValue(row.Key, out var b) && b.Value.Number is { } raw
                    ? new RateBaseline(raw, firstAt, first.UptimeTicks)
                    : null;
                var computed = MonitorMath.Compute(spec, row.Value, row.Denominator, at, reading.UptimeTicks, baseline);
                var filteredOut = allRows && !RuleEvaluator.KeepRow(spec, row.Filter, row.Label);
                if (computed.Value is not { } value)
                {
                    result.Add(new MonitorTestRow(row.Key, row.Label, "", Severity.Unknown, computed.Problem ?? "no value", filteredOut));
                    continue;
                }

                var (state, rule) = RuleEvaluator.Evaluate(spec, value);
                var why = rule is { } i ? $"rule {i + 1}" : "no rule matched: default";
                result.Add(new MonitorTestRow(row.Key, row.Label, MonitorMath.Display(value, spec.ValueMap, spec.Unit), state,
                    filteredOut ? why + " (skipped by the row filter)" : why, filteredOut));
            }

            return new MonitorTestResult(result, rows.Count, twice, rows.Count == 0 ? "The walk found no rows under that OID." : null);
        }
        catch (SnmpQueryException ex)
        {
            return MonitorTestResult.Fail(ex.Message);
        }
    }

    /// <summary>The rows a table monitor can be pointed at on a node, with their labels (from the label column, else the value).</summary>
    public async Task<(IReadOnlyList<InstanceChoice> Choices, string? Error)> ListInstancesAsync(MonitorSpec spec, int nodeId, CancellationToken cancellationToken = default)
    {
        if (spec.OidMode != OidMode.Table)
        {
            return ([], "Only table monitors have instances.");
        }

        var resolved = await targets.ResolveAsync(nodeId, cancellationToken);
        if (resolved.Target is not { } target)
        {
            return ([], resolved.Error);
        }

        try
        {
            var column = spec.LabelOid ?? spec.Oid;
            var walk = await snmp.WalkAsync(target, [column], MonitorCollector.MaxTableRows, cancellationToken);
            var choices = walk.Columns[0].Values
                .Select(v => new InstanceChoice(v.Oid.TrimStart('.')[(column.Length + 1)..], v.Text))
                .ToList();
            return (choices, choices.Count == 0 ? "No rows found on this node." : null);
        }
        catch (SnmpQueryException ex)
        {
            return ([], ex.Message);
        }
    }

    /// <summary>
    /// Applies a table monitor to one row of a node (from the interfaces tab or the picker),
    /// reading the row's current label so a later renumbering is noticed.
    /// </summary>
    public async Task<MonitorResult> AddInstanceAsync(int definitionId, int nodeId, string instance, CancellationToken cancellationToken = default)
    {
        if (await monitors.GetSpecAsync(definitionId, cancellationToken) is not { } spec)
        {
            return MonitorResult.Fail("The monitor no longer exists.");
        }

        string? label = null;
        if (spec.LabelOid is { } labelOid && MonitorValidation.IsInstanceSuffix(instance))
        {
            var resolved = await targets.ResolveAsync(nodeId, cancellationToken);
            if (resolved.Target is { } target)
            {
                try
                {
                    var v = (await snmp.GetAsync(target, [$"{labelOid}.{instance}"], cancellationToken)).Values[0];
                    label = v.IsMissing ? null : v.Text;
                }
                catch (SnmpQueryException)
                {
                    // Applying still works; the label check just starts with nothing to compare.
                }
            }
        }

        return await monitors.AddAssignmentAsync(definitionId, AssignmentScope.Node, nodeId, null, instance, label, cancellationToken);
    }
}
