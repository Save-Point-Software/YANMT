using Yanmt.Core.Status;
using Yanmt.Infrastructure.Data.Entities;

namespace Yanmt.Infrastructure.Monitoring;

/// <summary>
/// Feeds StatusSource rows to <see cref="NodeColorRollup"/>. The cache and the DB fallback in
/// NodeService both go through here, so the two can't drift.
/// </summary>
public static class NodeColors
{
    public static NodeColorRole RoleOf(StatusSourceType type) => type switch
    {
        StatusSourceType.Icmp => NodeColorRole.Icmp,
        StatusSourceType.Uptime => NodeColorRole.Snmp,
        _ => NodeColorRole.Other,
    };

    /// <param name="snmpEnabled">SNMP on and a profile assigned (what the SNMP poller polls).</param>
    /// <param name="sources">The node's enabled sources.</param>
    public static NodeColor Evaluate(bool nodeEnabled, bool icmpEnabled, bool snmpEnabled,
        IEnumerable<(StatusSourceType Type, Severity State, DateTime SinceUtc, DateTime? LastUpdateUtc)> sources) =>
        NodeColorRollup.Evaluate(nodeEnabled, icmpEnabled, snmpEnabled,
            sources.Select(s => new NodeColorSource(RoleOf(s.Type), s.State, s.SinceUtc, s.LastUpdateUtc)));
}
