using Yanmt.Core.Monitoring;
using Yanmt.Infrastructure.Collectors.Snmp;

namespace Yanmt.Infrastructure.Monitoring;

/// <summary>The system group as read in one poll. Everything is optional: an SNMP view may hide any of it.</summary>
public sealed record SystemInfoReading(string? Descr, string? ObjectId, string? Name, string? Location, string? Contact, uint? UptimeTicks);

/// <summary>Everything one default-stats poll read from a device.</summary>
public sealed record DefaultStatsReading(
    SystemInfoReading System,
    SnmpEngineInfo Engine,
    IReadOnlyList<decimal> CpuFiveMinute,
    IReadOnlyList<MemoryPool> MemoryPools);

/// <summary>
/// Reads the fixed default stats in as few requests as possible: one GET for the system group,
/// one GETBULK walk for the CPU and enhanced-mempool columns together, and, only when the enhanced
/// mempool table is empty, a walk of the older CISCO-MEMORY-POOL-MIB.
/// </summary>
public sealed class DefaultStatsCollector(ISnmpReader snmp)
{
    private const string SysDescr = "1.3.6.1.2.1.1.1.0";
    private const string SysObjectId = "1.3.6.1.2.1.1.2.0";
    private const string SysUpTime = "1.3.6.1.2.1.1.3.0";
    private const string SysContact = "1.3.6.1.2.1.1.4.0";
    private const string SysName = "1.3.6.1.2.1.1.5.0";
    private const string SysLocation = "1.3.6.1.2.1.1.6.0";

    private const string CpmCpuTotal5MinRev = "1.3.6.1.4.1.9.9.109.1.1.1.1.8";
    private const string CempMemPoolName = "1.3.6.1.4.1.9.9.221.1.1.1.1.3";
    private const string CempMemPoolUsed = "1.3.6.1.4.1.9.9.221.1.1.1.1.7";
    private const string CempMemPoolFree = "1.3.6.1.4.1.9.9.221.1.1.1.1.8";
    private const string CempMemPoolHcUsed = "1.3.6.1.4.1.9.9.221.1.1.1.1.18";
    private const string CempMemPoolHcFree = "1.3.6.1.4.1.9.9.221.1.1.1.1.20";
    private const string CiscoMemoryPoolName = "1.3.6.1.4.1.9.9.48.1.1.1.2";
    private const string CiscoMemoryPoolUsed = "1.3.6.1.4.1.9.9.48.1.1.1.5";
    private const string CiscoMemoryPoolFree = "1.3.6.1.4.1.9.9.48.1.1.1.6";

    /// <summary>Plenty for CPU and pool tables (a handful of rows each), and bounds a misbehaving agent.</summary>
    private const int MaxRows = 64;

    public async Task<DefaultStatsReading> CollectAsync(SnmpTarget target, CancellationToken cancellationToken)
    {
        var system = await snmp.GetAsync(target, [SysDescr, SysObjectId, SysUpTime, SysContact, SysName, SysLocation], cancellationToken);
        var v = system.Values.ToDictionary(x => x.Oid.TrimStart('.'));
        string? Text(string oid) => v.TryGetValue(oid, out var x) && !x.IsMissing ? x.Text : null;
        uint? uptime = v.TryGetValue(SysUpTime, out var up) && up is { IsMissing: false, Number: { } ticks } ? (uint)ticks : null;
        var info = new SystemInfoReading(Text(SysDescr), Text(SysObjectId), Text(SysName), Text(SysLocation), Text(SysContact), uptime);

        var walk = await snmp.WalkAsync(target,
            [CpmCpuTotal5MinRev, CempMemPoolName, CempMemPoolUsed, CempMemPoolFree, CempMemPoolHcUsed, CempMemPoolHcFree], MaxRows, cancellationToken);
        var cpu = walk[CpmCpuTotal5MinRev].Values.Where(x => x.Number is not null).Select(x => x.Number!.Value).ToList();

        var names = ByIndex(walk[CempMemPoolName]);
        List<MemoryPool> pools;
        if (names.Count > 0)
        {
            var used = Numbers(walk[CempMemPoolUsed]);
            var free = Numbers(walk[CempMemPoolFree]);
            var hcUsed = Numbers(walk[CempMemPoolHcUsed]);
            var hcFree = Numbers(walk[CempMemPoolHcFree]);
            pools = names.Select(n => new MemoryPool(n.Value.Text, Get(used, n.Key), Get(free, n.Key), Get(hcUsed, n.Key), Get(hcFree, n.Key))).ToList();
        }
        else
        {
            var legacy = await snmp.WalkAsync(target, [CiscoMemoryPoolName, CiscoMemoryPoolUsed, CiscoMemoryPoolFree], MaxRows, cancellationToken);
            var used = Numbers(legacy[CiscoMemoryPoolUsed]);
            var free = Numbers(legacy[CiscoMemoryPoolFree]);
            pools = ByIndex(legacy[CiscoMemoryPoolName]).Select(n => new MemoryPool(n.Value.Text, Get(used, n.Key), Get(free, n.Key))).ToList();
        }

        // The walk's engine info is the latest; the GET's is the same engine moments earlier.
        return new DefaultStatsReading(info, walk.Engine, cpu, pools);
    }

    /// <summary>Row index (the OID suffix after the column) to value.</summary>
    private static Dictionary<string, SnmpValue> ByIndex(SnmpWalkColumn column) =>
        column.Values.ToDictionary(x => x.Oid.TrimStart('.')[(column.Root.Length + 1)..]);

    private static Dictionary<string, decimal> Numbers(SnmpWalkColumn column) =>
        ByIndex(column).Where(x => x.Value.Number is not null).ToDictionary(x => x.Key, x => x.Value.Number!.Value);

    private static decimal? Get(Dictionary<string, decimal> d, string index) => d.TryGetValue(index, out var x) ? x : null;
}
