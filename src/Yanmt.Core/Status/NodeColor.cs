namespace Yanmt.Core.Status;

/// <summary>
/// The color a node (or submap) shows on the map and in lists: the kind of problem, not the
/// worst severity. Separate from <see cref="Severity"/>, which sources, events and links keep.
/// </summary>
public enum NodeColor
{
    /// <summary>Gray: the node is disabled, or nothing has reported yet.</summary>
    NoData,

    /// <summary>Green: everything that reports is Normal.</summary>
    Normal,

    /// <summary>Yellow: the node answers, but something on it is not Normal.</summary>
    Attention,

    /// <summary>Orange: ICMP answers but SNMP doesn't, so we can't see inside the node.</summary>
    NoSnmp,

    /// <summary>Red: neither ICMP nor SNMP answers (or the only one in use doesn't).</summary>
    Down,
}

public static class NodeColorExtensions
{
    /// <summary>Rollup rank for submaps: red &gt; orange &gt; yellow &gt; green &gt; gray.</summary>
    public static int Rank(this NodeColor color) => color switch
    {
        NodeColor.NoData => 0,
        NodeColor.Normal => 1,
        NodeColor.Attention => 2,
        NodeColor.NoSnmp => 3,
        NodeColor.Down => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(color), color, null),
    };

    public static NodeColor WorseOf(this NodeColor a, NodeColor b) => a.Rank() >= b.Rank() ? a : b;

    /// <summary>Worst color of a set; empty is <see cref="NodeColor.NoData"/>.</summary>
    public static NodeColor Worst(IEnumerable<NodeColor> colors) =>
        colors.Aggregate(NodeColor.NoData, (worst, c) => worst.WorseOf(c));

    /// <summary>Label shown next to the color: Down / No SNMP / Needs attention / Normal / No data.</summary>
    public static string Label(this NodeColor color) => color switch
    {
        NodeColor.Down => "Down",
        NodeColor.NoSnmp => "No SNMP",
        NodeColor.Attention => "Needs attention",
        NodeColor.Normal => "Normal",
        _ => "No data",
    };
}

/// <summary>What a status source is to the node color rules.</summary>
public enum NodeColorRole
{
    Icmp,

    /// <summary>The SNMP Uptime source, which stands for "SNMP polling works".</summary>
    Snmp,

    /// <summary>Everything else: CPU, memory, custom monitors, backups.</summary>
    Other,
}

/// <summary>One enabled source of a node. <paramref name="LastUpdateUtc"/> is null until it has reported.</summary>
public readonly record struct NodeColorSource(NodeColorRole Role, Severity State, DateTime SinceUtc, DateTime? LastUpdateUtc);

/// <summary>
/// Node color rules, first match wins (see docs/SPEC.md, Status model). Sources that have never
/// reported are left out of every rule.
/// <list type="number">
/// <item>Gray: the node is disabled, or no source has reported.</item>
/// <item>Red: ICMP is Critical and SNMP has not answered since ICMP went Critical (SNMP failing,
/// or not polled since). With only one of ICMP and SNMP in use, that one failing is enough.</item>
/// <item>Orange: ICMP answers (Normal or Warning) and SNMP is not responding. Other sources going
/// Unknown at the same time are ignored.</item>
/// <item>Yellow: any other source is not Normal (including ICMP Critical while SNMP answers).</item>
/// <item>Green.</item>
/// </list>
/// "SNMP not responding" is the Uptime source Unknown after a poll. ICMP Unknown (the probe itself
/// failed) is no evidence either way, so for red and orange it counts as ICMP not in use.
/// </summary>
public static class NodeColorRollup
{
    public static NodeColor Evaluate(bool nodeEnabled, bool icmpEnabled, bool snmpEnabled, IEnumerable<NodeColorSource> sources)
    {
        if (!nodeEnabled)
        {
            return NodeColor.NoData;
        }

        // A protocol that's switched off doesn't count, even if a stale source is still around.
        var reported = sources
            .Where(s => s.LastUpdateUtc is not null)
            .Where(s => s.Role != NodeColorRole.Icmp || icmpEnabled)
            .Where(s => s.Role != NodeColorRole.Snmp || snmpEnabled)
            .ToList();
        if (reported.Count == 0)
        {
            return NodeColor.NoData;
        }

        var icmp = Find(reported, NodeColorRole.Icmp);
        if (icmp is { State: Severity.Unknown })
        {
            icmp = null;
        }

        var snmp = Find(reported, NodeColorRole.Snmp);
        var snmpDown = snmp is { State: Severity.Unknown };

        if (icmp is { State: Severity.Critical } down)
        {
            var snmpAnsweredSince = snmp is { State: not Severity.Unknown, LastUpdateUtc: { } at } && at >= down.SinceUtc;
            if (!snmpAnsweredSince)
            {
                return NodeColor.Down;
            }
        }
        else if (snmpDown)
        {
            return icmp is null ? NodeColor.Down : NodeColor.NoSnmp;
        }

        return reported.Any(s => s.State != Severity.Normal) ? NodeColor.Attention : NodeColor.Normal;
    }

    // The sources are structs, so FirstOrDefault would hand back a default (Normal) one, not null.
    private static NodeColorSource? Find(List<NodeColorSource> sources, NodeColorRole role)
    {
        foreach (var s in sources)
        {
            if (s.Role == role)
            {
                return s;
            }
        }

        return null;
    }
}
