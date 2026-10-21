using System.Text.RegularExpressions;

namespace Yanmt.Core.Oids;

/// <summary>A named object from a MIB: e.g. ifDescr = 1.3.6.1.2.1.2.2.1.2 in IF-MIB.</summary>
public sealed record OidName(string Name, string Oid, string Module);

/// <summary>
/// Turns numeric OIDs into readable names and back. v1 ships a built-in table; a MIB importer
/// (out of scope for v1) would be another implementation of this interface.
/// </summary>
public interface IOidNameResolver
{
    /// <summary>The longest known prefix of <paramref name="oid"/>, e.g. "ifDescr.10001", or null if nothing matches.</summary>
    string? Describe(string oid);

    /// <summary>
    /// Accepts a numeric OID (with or without a leading dot), a name ("ifDescr"), a name with an
    /// instance ("ifDescr.10001"), or a module-qualified name ("IF-MIB::ifDescr.3"), and returns the numeric OID.
    /// </summary>
    bool TryParse(string text, out string oid);

    IReadOnlyList<OidName> All { get; }
}

/// <summary>The built-in table: SNMPv2-MIB, IF-MIB, BGP4-MIB, and the Cisco CPU and memory MIBs.</summary>
public sealed partial class BuiltInOidNames : IOidNameResolver
{
    private static readonly OidName[] Table =
    [
        new("iso", "1", "SNMPv2-SMI"),
        new("internet", "1.3.6.1", "SNMPv2-SMI"),
        new("mib-2", "1.3.6.1.2.1", "SNMPv2-SMI"),
        new("enterprises", "1.3.6.1.4.1", "SNMPv2-SMI"),
        new("cisco", "1.3.6.1.4.1.9", "CISCO-SMI"),

        new("system", "1.3.6.1.2.1.1", "SNMPv2-MIB"),
        new("sysDescr", "1.3.6.1.2.1.1.1", "SNMPv2-MIB"),
        new("sysObjectID", "1.3.6.1.2.1.1.2", "SNMPv2-MIB"),
        new("sysUpTime", "1.3.6.1.2.1.1.3", "SNMPv2-MIB"),
        new("sysContact", "1.3.6.1.2.1.1.4", "SNMPv2-MIB"),
        new("sysName", "1.3.6.1.2.1.1.5", "SNMPv2-MIB"),
        new("sysLocation", "1.3.6.1.2.1.1.6", "SNMPv2-MIB"),
        new("sysServices", "1.3.6.1.2.1.1.7", "SNMPv2-MIB"),
        new("snmpEngineID", "1.3.6.1.6.3.10.2.1.1", "SNMP-FRAMEWORK-MIB"),
        new("snmpEngineBoots", "1.3.6.1.6.3.10.2.1.2", "SNMP-FRAMEWORK-MIB"),
        new("snmpEngineTime", "1.3.6.1.6.3.10.2.1.3", "SNMP-FRAMEWORK-MIB"),

        new("interfaces", "1.3.6.1.2.1.2", "IF-MIB"),
        new("ifNumber", "1.3.6.1.2.1.2.1", "IF-MIB"),
        new("ifTable", "1.3.6.1.2.1.2.2", "IF-MIB"),
        new("ifEntry", "1.3.6.1.2.1.2.2.1", "IF-MIB"),
        new("ifIndex", "1.3.6.1.2.1.2.2.1.1", "IF-MIB"),
        new("ifDescr", "1.3.6.1.2.1.2.2.1.2", "IF-MIB"),
        new("ifType", "1.3.6.1.2.1.2.2.1.3", "IF-MIB"),
        new("ifMtu", "1.3.6.1.2.1.2.2.1.4", "IF-MIB"),
        new("ifSpeed", "1.3.6.1.2.1.2.2.1.5", "IF-MIB"),
        new("ifPhysAddress", "1.3.6.1.2.1.2.2.1.6", "IF-MIB"),
        new("ifAdminStatus", "1.3.6.1.2.1.2.2.1.7", "IF-MIB"),
        new("ifOperStatus", "1.3.6.1.2.1.2.2.1.8", "IF-MIB"),
        new("ifLastChange", "1.3.6.1.2.1.2.2.1.9", "IF-MIB"),
        new("ifInOctets", "1.3.6.1.2.1.2.2.1.10", "IF-MIB"),
        new("ifInUcastPkts", "1.3.6.1.2.1.2.2.1.11", "IF-MIB"),
        new("ifInDiscards", "1.3.6.1.2.1.2.2.1.13", "IF-MIB"),
        new("ifInErrors", "1.3.6.1.2.1.2.2.1.14", "IF-MIB"),
        new("ifInUnknownProtos", "1.3.6.1.2.1.2.2.1.15", "IF-MIB"),
        new("ifOutOctets", "1.3.6.1.2.1.2.2.1.16", "IF-MIB"),
        new("ifOutUcastPkts", "1.3.6.1.2.1.2.2.1.17", "IF-MIB"),
        new("ifOutDiscards", "1.3.6.1.2.1.2.2.1.19", "IF-MIB"),
        new("ifOutErrors", "1.3.6.1.2.1.2.2.1.20", "IF-MIB"),
        new("ifXTable", "1.3.6.1.2.1.31.1.1", "IF-MIB"),
        new("ifXEntry", "1.3.6.1.2.1.31.1.1.1", "IF-MIB"),
        new("ifName", "1.3.6.1.2.1.31.1.1.1.1", "IF-MIB"),
        new("ifInMulticastPkts", "1.3.6.1.2.1.31.1.1.1.2", "IF-MIB"),
        new("ifInBroadcastPkts", "1.3.6.1.2.1.31.1.1.1.3", "IF-MIB"),
        new("ifOutMulticastPkts", "1.3.6.1.2.1.31.1.1.1.4", "IF-MIB"),
        new("ifOutBroadcastPkts", "1.3.6.1.2.1.31.1.1.1.5", "IF-MIB"),
        new("ifHCInOctets", "1.3.6.1.2.1.31.1.1.1.6", "IF-MIB"),
        new("ifHCInUcastPkts", "1.3.6.1.2.1.31.1.1.1.7", "IF-MIB"),
        new("ifHCInMulticastPkts", "1.3.6.1.2.1.31.1.1.1.8", "IF-MIB"),
        new("ifHCInBroadcastPkts", "1.3.6.1.2.1.31.1.1.1.9", "IF-MIB"),
        new("ifHCOutOctets", "1.3.6.1.2.1.31.1.1.1.10", "IF-MIB"),
        new("ifHCOutUcastPkts", "1.3.6.1.2.1.31.1.1.1.11", "IF-MIB"),
        new("ifHCOutMulticastPkts", "1.3.6.1.2.1.31.1.1.1.12", "IF-MIB"),
        new("ifHCOutBroadcastPkts", "1.3.6.1.2.1.31.1.1.1.13", "IF-MIB"),
        new("ifLinkUpDownTrapEnable", "1.3.6.1.2.1.31.1.1.1.14", "IF-MIB"),
        new("ifHighSpeed", "1.3.6.1.2.1.31.1.1.1.15", "IF-MIB"),
        new("ifPromiscuousMode", "1.3.6.1.2.1.31.1.1.1.16", "IF-MIB"),
        new("ifConnectorPresent", "1.3.6.1.2.1.31.1.1.1.17", "IF-MIB"),
        new("ifAlias", "1.3.6.1.2.1.31.1.1.1.18", "IF-MIB"),
        new("ifCounterDiscontinuityTime", "1.3.6.1.2.1.31.1.1.1.19", "IF-MIB"),

        new("bgp", "1.3.6.1.2.1.15", "BGP4-MIB"),
        new("bgpVersion", "1.3.6.1.2.1.15.1", "BGP4-MIB"),
        new("bgpLocalAs", "1.3.6.1.2.1.15.2", "BGP4-MIB"),
        new("bgpPeerTable", "1.3.6.1.2.1.15.3", "BGP4-MIB"),
        new("bgpPeerEntry", "1.3.6.1.2.1.15.3.1", "BGP4-MIB"),
        new("bgpPeerIdentifier", "1.3.6.1.2.1.15.3.1.1", "BGP4-MIB"),
        new("bgpPeerState", "1.3.6.1.2.1.15.3.1.2", "BGP4-MIB"),
        new("bgpPeerAdminStatus", "1.3.6.1.2.1.15.3.1.3", "BGP4-MIB"),
        new("bgpPeerNegotiatedVersion", "1.3.6.1.2.1.15.3.1.4", "BGP4-MIB"),
        new("bgpPeerLocalAddr", "1.3.6.1.2.1.15.3.1.5", "BGP4-MIB"),
        new("bgpPeerLocalPort", "1.3.6.1.2.1.15.3.1.6", "BGP4-MIB"),
        new("bgpPeerRemoteAddr", "1.3.6.1.2.1.15.3.1.7", "BGP4-MIB"),
        new("bgpPeerRemotePort", "1.3.6.1.2.1.15.3.1.8", "BGP4-MIB"),
        new("bgpPeerRemoteAs", "1.3.6.1.2.1.15.3.1.9", "BGP4-MIB"),
        new("bgpPeerInUpdates", "1.3.6.1.2.1.15.3.1.10", "BGP4-MIB"),
        new("bgpPeerOutUpdates", "1.3.6.1.2.1.15.3.1.11", "BGP4-MIB"),
        new("bgpPeerInTotalMessages", "1.3.6.1.2.1.15.3.1.12", "BGP4-MIB"),
        new("bgpPeerOutTotalMessages", "1.3.6.1.2.1.15.3.1.13", "BGP4-MIB"),
        new("bgpPeerLastError", "1.3.6.1.2.1.15.3.1.14", "BGP4-MIB"),
        new("bgpPeerFsmEstablishedTransitions", "1.3.6.1.2.1.15.3.1.15", "BGP4-MIB"),
        new("bgpPeerFsmEstablishedTime", "1.3.6.1.2.1.15.3.1.16", "BGP4-MIB"),
        new("bgpPeerInUpdateElapsedTime", "1.3.6.1.2.1.15.3.1.24", "BGP4-MIB"),
        new("bgpIdentifier", "1.3.6.1.2.1.15.4", "BGP4-MIB"),

        new("cpmCPUTotalTable", "1.3.6.1.4.1.9.9.109.1.1.1", "CISCO-PROCESS-MIB"),
        new("cpmCPUTotalEntry", "1.3.6.1.4.1.9.9.109.1.1.1.1", "CISCO-PROCESS-MIB"),
        new("cpmCPUTotalPhysicalIndex", "1.3.6.1.4.1.9.9.109.1.1.1.1.2", "CISCO-PROCESS-MIB"),
        new("cpmCPUTotal5secRev", "1.3.6.1.4.1.9.9.109.1.1.1.1.6", "CISCO-PROCESS-MIB"),
        new("cpmCPUTotal1minRev", "1.3.6.1.4.1.9.9.109.1.1.1.1.7", "CISCO-PROCESS-MIB"),
        new("cpmCPUTotal5minRev", "1.3.6.1.4.1.9.9.109.1.1.1.1.8", "CISCO-PROCESS-MIB"),
        new("cpmCPUMemoryUsed", "1.3.6.1.4.1.9.9.109.1.1.1.1.12", "CISCO-PROCESS-MIB"),
        new("cpmCPUMemoryFree", "1.3.6.1.4.1.9.9.109.1.1.1.1.13", "CISCO-PROCESS-MIB"),

        new("cempMemPoolTable", "1.3.6.1.4.1.9.9.221.1.1.1", "CISCO-ENHANCED-MEMPOOL-MIB"),
        new("cempMemPoolEntry", "1.3.6.1.4.1.9.9.221.1.1.1.1", "CISCO-ENHANCED-MEMPOOL-MIB"),
        new("cempMemPoolType", "1.3.6.1.4.1.9.9.221.1.1.1.1.2", "CISCO-ENHANCED-MEMPOOL-MIB"),
        new("cempMemPoolName", "1.3.6.1.4.1.9.9.221.1.1.1.1.3", "CISCO-ENHANCED-MEMPOOL-MIB"),
        new("cempMemPoolValid", "1.3.6.1.4.1.9.9.221.1.1.1.1.6", "CISCO-ENHANCED-MEMPOOL-MIB"),
        new("cempMemPoolUsed", "1.3.6.1.4.1.9.9.221.1.1.1.1.7", "CISCO-ENHANCED-MEMPOOL-MIB"),
        new("cempMemPoolFree", "1.3.6.1.4.1.9.9.221.1.1.1.1.8", "CISCO-ENHANCED-MEMPOOL-MIB"),
        new("cempMemPoolLargestFree", "1.3.6.1.4.1.9.9.221.1.1.1.1.9", "CISCO-ENHANCED-MEMPOOL-MIB"),
        new("cempMemPoolLowestFree", "1.3.6.1.4.1.9.9.221.1.1.1.1.10", "CISCO-ENHANCED-MEMPOOL-MIB"),
        new("cempMemPoolHCUsed", "1.3.6.1.4.1.9.9.221.1.1.1.1.18", "CISCO-ENHANCED-MEMPOOL-MIB"),
        new("cempMemPoolHCFree", "1.3.6.1.4.1.9.9.221.1.1.1.1.20", "CISCO-ENHANCED-MEMPOOL-MIB"),

        new("ciscoMemoryPoolTable", "1.3.6.1.4.1.9.9.48.1.1", "CISCO-MEMORY-POOL-MIB"),
        new("ciscoMemoryPoolEntry", "1.3.6.1.4.1.9.9.48.1.1.1", "CISCO-MEMORY-POOL-MIB"),
        new("ciscoMemoryPoolName", "1.3.6.1.4.1.9.9.48.1.1.1.2", "CISCO-MEMORY-POOL-MIB"),
        new("ciscoMemoryPoolValid", "1.3.6.1.4.1.9.9.48.1.1.1.4", "CISCO-MEMORY-POOL-MIB"),
        new("ciscoMemoryPoolUsed", "1.3.6.1.4.1.9.9.48.1.1.1.5", "CISCO-MEMORY-POOL-MIB"),
        new("ciscoMemoryPoolFree", "1.3.6.1.4.1.9.9.48.1.1.1.6", "CISCO-MEMORY-POOL-MIB"),
        new("ciscoMemoryPoolLargestFree", "1.3.6.1.4.1.9.9.48.1.1.1.7", "CISCO-MEMORY-POOL-MIB"),
    ];

    // Declared after Table: static initializers run in order, and the constructor reads Table.
    public static BuiltInOidNames Instance { get; } = new();

    private readonly Dictionary<string, OidName> _byOid = Table.ToDictionary(n => n.Oid);
    private readonly Dictionary<string, OidName> _byName = Table.ToDictionary(n => n.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<OidName> All => Table;

    public string? Describe(string oid)
    {
        var numeric = oid.Trim().TrimStart('.');
        for (var prefix = numeric; prefix.Length > 0;)
        {
            if (_byOid.TryGetValue(prefix, out var name))
            {
                return prefix.Length == numeric.Length ? name.Name : name.Name + numeric[prefix.Length..];
            }

            var dot = prefix.LastIndexOf('.');
            if (dot < 0)
            {
                break;
            }

            prefix = prefix[..dot];
        }

        return null;
    }

    public bool TryParse(string text, out string oid)
    {
        oid = "";
        var t = text.Trim();
        if (t.Length == 0)
        {
            return false;
        }

        if (Numeric().IsMatch(t))
        {
            oid = t.TrimStart('.');
            return true;
        }

        var qualified = t.IndexOf("::", StringComparison.Ordinal);
        if (qualified >= 0)
        {
            t = t[(qualified + 2)..];
        }

        var dot = t.IndexOf('.');
        var name = dot < 0 ? t : t[..dot];
        var suffix = dot < 0 ? "" : t[dot..];
        if (!_byName.TryGetValue(name, out var known) || (suffix.Length > 0 && !NumericSuffix().IsMatch(suffix)))
        {
            return false;
        }

        oid = known.Oid + suffix;
        return true;
    }

    [GeneratedRegex(@"^\.?\d+(\.\d+)*$")]
    private static partial Regex Numeric();

    [GeneratedRegex(@"^(\.\d+)+$")]
    private static partial Regex NumericSuffix();
}
