using System.Net;
using Yanmt.Core.Monitoring;
using Yanmt.Infrastructure.Collectors.Snmp;
using Yanmt.Infrastructure.Monitoring;

namespace Yanmt.Integration.Tests;

/// <summary>
/// Phase 3 against real IOS-XE: the default stats (system group, CPU, memory pools) and a live
/// IF-MIB walk with two counter samples. Capability-aware like <see cref="SnmpLabTests"/>: each
/// node is read with the first supported profile it lists.
/// </summary>
[Trait("Category", "Integration")]
[TestCaseOrderer(typeof(AlphabeticalOrderer))]
public class DefaultStatsLabTests
{
    private const string IfName = "1.3.6.1.2.1.31.1.1.1.1";
    private const string IfOperStatus = "1.3.6.1.2.1.2.2.1.8";
    private const string IfHcInOctets = "1.3.6.1.2.1.31.1.1.1.6";
    private const string IfHcOutOctets = "1.3.6.1.2.1.31.1.1.1.10";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Nodes with a usable (supported) SNMP profile, and the target to reach each.</summary>
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
    public async Task Stats_1_default_stats_read_on_every_snmp_node()
    {
        var reader = new SnmpV3Reader();
        var collector = new DefaultStatsCollector(reader);
        var failures = new List<string>();
        var output = TestContext.Current.TestOutputHelper;

        foreach (var (node, target) in Targets())
        {
            try
            {
                var r = await collector.CollectAsync(target, Ct);
                var memory = DefaultStats.PickMemory(r.MemoryPools);
                output?.WriteLine(
                    $"{node.Hostname}: sysName={r.System.Name}, uptime={(r.System.UptimeTicks is { } t ? Units.Uptime(t) : "?")}, " +
                    $"engine boots={r.Engine.Boots}, CPU 5min={string.Join("/", r.CpuFiveMinute)}%, " +
                    $"pools=[{string.Join(", ", r.MemoryPools.Select(p => $"{p.Name}: used {p.HcUsed ?? p.Used} free {p.HcFree ?? p.Free}"))}], " +
                    $"memory={memory?.Pool} {memory?.Percent:0.#}%");

                if (r.System.UptimeTicks is null)
                {
                    failures.Add($"{node.Hostname}: sysUpTime missing");
                }

                if (r.CpuFiveMinute.Count == 0)
                {
                    failures.Add($"{node.Hostname}: no cpmCPUTotal5minRev rows (check the SNMP view includes 1.3.6.1.4.1.9.9.109)");
                }

                if (memory is null)
                {
                    failures.Add($"{node.Hostname}: no usable memory pool (CISCO-ENHANCED-MEMPOOL-MIB or CISCO-MEMORY-POOL-MIB)");
                }
            }
            catch (SnmpQueryException ex)
            {
                failures.Add($"{node.Hostname}: {ex.Category}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, "Default stats incomplete:\n" + string.Join("\n", failures));
    }

    [Fact]
    public async Task Stats_2_interfaces_walk_and_two_counter_samples_give_rates()
    {
        var (node, target) = Targets()[0];
        var reader = new SnmpV3Reader();
        var output = TestContext.Current.TestOutputHelper;

        var first = await reader.WalkAsync(target, [IfName, IfOperStatus, IfHcInOctets, IfHcOutOctets], 2000, Ct);
        var started = DateTime.UtcNow;
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        var second = await reader.WalkAsync(target, [IfHcInOctets, IfHcOutOctets], 2000, Ct);
        var seconds = (DateTime.UtcNow - started).TotalSeconds;

        var names = first[IfName].Values;
        output?.WriteLine($"{node.Hostname}: {names.Count} interfaces in {first.Requests} GETBULK request(s); then {second.Requests} for the counters");
        Assert.NotEmpty(names);
        Assert.Equal(names.Count, first[IfHcInOctets].Values.Count);

        var inBefore = first[IfHcInOctets].Values;
        var inAfter = second[IfHcInOctets].Values.ToDictionary(v => v.Oid);
        for (var i = 0; i < names.Count; i++)
        {
            var after = inAfter.GetValueOrDefault(inBefore[i].Oid);
            var rate = after is null ? null : RateMath.BitsPerSecond(inBefore[i].Number!.Value, after.Number!.Value, 64, seconds);
            output?.WriteLine($"  {names[i].Text}: oper={first[IfOperStatus].Values[i].Text} in={(rate is { } r ? Units.BitsPerSecond(r) : "?")}");
            Assert.True(rate is null or >= 0, "A rate can't be negative.");
        }
    }
}
