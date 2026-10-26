using System.Net;
using Yanmt.Core.Monitors;
using Yanmt.Infrastructure.Collectors.Snmp;
using Yanmt.Infrastructure.Monitors;

namespace Yanmt.Integration.Tests;

/// <summary>
/// Phase 4 against real IOS-XE: the starter-library monitors read through the real collector and
/// evaluated with the real rules (nothing is stored). Prints every row so the results can be
/// compared with the device's own show commands.
/// </summary>
[Trait("Category", "Integration")]
[TestCaseOrderer(typeof(AlphabeticalOrderer))]
public class CustomMonitorLabTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static List<(LabNode Node, SnmpTarget Target)> Targets()
    {
        var lab = LabConfig.Require();
        var targets = new List<(LabNode, SnmpTarget)>();
        foreach (var node in lab.Nodes)
        {
            var key = node.SnmpProfiles.FirstOrDefault(k => !k.Equals(LabSnmp.Sha256Aes256Key, StringComparison.OrdinalIgnoreCase));
            if (key is not null && lab.Snmp?.Credential(key) is { } cred)
            {
                targets.Add((node, new SnmpTarget(IPAddress.Parse(node.ManagementIp), lab.Snmp.Port, cred, TimeSpan.FromSeconds(5))));
            }
        }

        Assert.SkipWhen(targets.Count == 0, "No node in lab.json lists a supported SNMP profile.");
        return targets;
    }

    [Fact]
    public async Task Monitors_1_starter_library_all_rows_on_every_snmp_node()
    {
        var specs = StarterLibrary.Specs();
        var works = specs.Select(s => new MonitorWork(s, AllRows: true, new Dictionary<string, string?>())).ToList();
        var collector = new MonitorCollector(new SnmpV3Reader());
        var output = TestContext.Current.TestOutputHelper;

        foreach (var (node, target) in Targets())
        {
            // Two reads 5 s apart so the rate monitors have a value.
            var first = await collector.CollectAsync(target, works, Ct);
            var firstAt = DateTime.UtcNow;
            await Task.Delay(TimeSpan.FromSeconds(5), Ct);
            var second = await collector.CollectAsync(target, works, Ct);
            var at = DateTime.UtcNow;

            output?.WriteLine($"{node.Hostname} (sysUpTime {second.UptimeTicks}):");
            foreach (var spec in specs)
            {
                var reading = second.Works.Single(w => w.DefinitionId == spec.Id);
                var before = first.Works.Single(w => w.DefinitionId == spec.Id).Rows.ToDictionary(r => r.Key);
                output?.WriteLine($"  {spec.Name}: {reading.Rows.Count} row(s)");
                foreach (var row in reading.Rows)
                {
                    var baseline = before.TryGetValue(row.Key, out var b) && b.Value.Number is { } raw ? new RateBaseline(raw, firstAt, first.UptimeTicks) : null;
                    var computed = MonitorMath.Compute(spec, row.Value, row.Denominator, at, second.UptimeTicks, baseline);
                    var kept = RuleEvaluator.KeepRow(spec, row.Filter, row.Label);
                    var state = computed.Value is { } v ? RuleEvaluator.Evaluate(spec, v).State.ToString() : "Unknown";
                    var shown = computed.Value is { } v2 ? MonitorMath.Display(v2, spec.ValueMap, spec.Unit) : computed.Problem;
                    output?.WriteLine($"    {row.Key,-16} {row.Label,-24} {shown,-22} {state,-8}{(kept ? "" : " (filtered out)")}");
                    Assert.True(computed.Value is not null || spec.ValueMode == ValueMode.Rate || row.Value.Missing, $"{spec.Name} {row.Key}: {computed.Problem}");
                }
            }

            // Every node has interfaces and CPUs.
            Assert.NotEmpty(second.Works.Single(w => w.DefinitionId == specs.Single(s => s.Name == "Interface oper status").Id).Rows);
            Assert.NotEmpty(second.Works.Single(w => w.DefinitionId == specs.Single(s => s.Name == "CPU 1-minute average").Id).Rows);
        }
    }

    [Fact]
    public async Task Monitors_2_picked_instance_and_scalar_share_one_get()
    {
        var (node, target) = Targets()[0];
        var spec = StarterLibrary.Specs().Single(s => s.Name == "Interface oper status");
        var scalar = new MonitorSpec { Id = 99, Name = "sysUpTime", OidMode = OidMode.Scalar, Oid = "1.3.6.1.2.1.1.3.0" };
        var works = new List<MonitorWork>
        {
            new(spec, false, new Dictionary<string, string?> { ["1"] = null }),
            new(scalar, false, new Dictionary<string, string?> { [""] = null }),
        };

        var reading = await new MonitorCollector(new SnmpV3Reader()).CollectAsync(target, works, Ct);

        var row = Assert.Single(reading.Works[0].Rows);
        TestContext.Current.TestOutputHelper?.WriteLine($"{node.Hostname}: ifIndex 1 = {row.Label}, oper {row.Value.Number}; sysUpTime {reading.Works[1].Rows[0].Value.Number}");
        Assert.False(row.Value.Missing);
        Assert.NotNull(row.Label);
        Assert.NotNull(reading.Works[1].Rows[0].Value.Number);
    }
}
