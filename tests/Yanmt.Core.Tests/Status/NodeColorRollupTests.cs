using Yanmt.Core.Status;

namespace Yanmt.Core.Tests.Status;

public class NodeColorRollupTests
{
    private static readonly DateTime T0 = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

    private static NodeColorSource Icmp(Severity s, int sinceMin = 0) => new(NodeColorRole.Icmp, s, T0.AddMinutes(sinceMin), T0.AddMinutes(10));

    /// <summary>The Uptime source; <paramref name="lastMin"/> is when SNMP last polled.</summary>
    private static NodeColorSource Snmp(Severity s, int lastMin = 10) => new(NodeColorRole.Snmp, s, T0, T0.AddMinutes(lastMin));

    private static NodeColorSource Other(Severity s) => new(NodeColorRole.Other, s, T0, T0.AddMinutes(10));

    private static NodeColorSource NeverReported(NodeColorRole role) => new(role, Severity.Unknown, T0, null);

    private static NodeColor Eval(params NodeColorSource[] sources) => NodeColorRollup.Evaluate(true, true, true, sources);

    [Fact]
    public void Disabled_node_is_gray() =>
        Assert.Equal(NodeColor.NoData, NodeColorRollup.Evaluate(false, true, true, [Icmp(Severity.Critical), Snmp(Severity.Unknown)]));

    [Fact]
    public void Nothing_reported_is_gray()
    {
        Assert.Equal(NodeColor.NoData, Eval());
        Assert.Equal(NodeColor.NoData, Eval(NeverReported(NodeColorRole.Icmp), NeverReported(NodeColorRole.Snmp), NeverReported(NodeColorRole.Other)));
    }

    [Fact]
    public void All_normal_is_green() =>
        Assert.Equal(NodeColor.Normal, Eval(Icmp(Severity.Normal), Snmp(Severity.Normal), Other(Severity.Normal)));

    [Fact]
    public void Never_reported_sources_are_left_out() =>
        Assert.Equal(NodeColor.Normal, Eval(Icmp(Severity.Normal), NeverReported(NodeColorRole.Snmp), NeverReported(NodeColorRole.Other)));

    // Red

    [Fact]
    public void Icmp_critical_and_snmp_failing_is_red() =>
        Assert.Equal(NodeColor.Down, Eval(Icmp(Severity.Critical), Snmp(Severity.Unknown), Other(Severity.Unknown)));

    [Fact]
    public void Icmp_only_node_is_red_when_icmp_critical()
    {
        Assert.Equal(NodeColor.Down, NodeColorRollup.Evaluate(true, true, false, [Icmp(Severity.Critical)]));
        Assert.Equal(NodeColor.Down, Eval(Icmp(Severity.Critical), NeverReported(NodeColorRole.Snmp)));
    }

    [Fact]
    public void Snmp_only_node_is_red_when_snmp_failing()
    {
        Assert.Equal(NodeColor.Down, NodeColorRollup.Evaluate(true, false, true, [Snmp(Severity.Unknown), Other(Severity.Unknown)]));
        Assert.Equal(NodeColor.Down, Eval(NeverReported(NodeColorRole.Icmp), Snmp(Severity.Unknown)));
    }

    [Fact]
    public void Icmp_probe_failure_is_no_evidence_so_snmp_failing_is_red() =>
        Assert.Equal(NodeColor.Down, Eval(Icmp(Severity.Unknown), Snmp(Severity.Unknown)));

    [Fact]
    public void Icmp_critical_before_snmp_has_answered_since_is_red()
    {
        // ICMP went Critical at minute 5; SNMP last answered at minute 3 (its next poll isn't due yet).
        Assert.Equal(NodeColor.Down, Eval(Icmp(Severity.Critical, sinceMin: 5), Snmp(Severity.Normal, lastMin: 3)));
    }

    [Fact]
    public void Icmp_critical_after_snmp_answers_is_yellow_ping_blocked()
    {
        Assert.Equal(NodeColor.Attention, Eval(Icmp(Severity.Critical, sinceMin: 5), Snmp(Severity.Normal, lastMin: 6)));
        Assert.Equal(NodeColor.Attention, Eval(Icmp(Severity.Critical, sinceMin: 5), Snmp(Severity.Normal, lastMin: 5)));
    }

    [Fact]
    public void Icmp_critical_with_snmp_disabled_ignores_a_stale_snmp_source() =>
        Assert.Equal(NodeColor.Down, NodeColorRollup.Evaluate(true, true, false, [Icmp(Severity.Critical, sinceMin: 5), Snmp(Severity.Normal, lastMin: 6)]));

    // Orange

    [Theory]
    [InlineData(Severity.Normal)]
    [InlineData(Severity.Warning)] // loss, latency, or a no-reply still pending Critical
    public void Icmp_answering_and_snmp_failing_is_orange(Severity icmp) =>
        Assert.Equal(NodeColor.NoSnmp, Eval(Icmp(icmp), Snmp(Severity.Unknown)));

    [Fact]
    public void Snmp_down_with_custom_monitors_cpu_and_memory_unknown_is_orange_not_yellow() =>
        Assert.Equal(NodeColor.NoSnmp, Eval(Icmp(Severity.Normal), Snmp(Severity.Unknown), Other(Severity.Unknown), Other(Severity.Unknown), Other(Severity.Critical)));

    // Yellow

    [Theory]
    [InlineData(Severity.Warning)]
    [InlineData(Severity.Critical)]
    [InlineData(Severity.Unknown)] // e.g. the OID vanished while SNMP still answers
    public void Custom_monitor_not_normal_is_yellow(Severity monitor) =>
        Assert.Equal(NodeColor.Attention, Eval(Icmp(Severity.Normal), Snmp(Severity.Normal), Other(monitor)));

    [Fact]
    public void Bgp_peer_down_on_a_reachable_router_is_yellow() =>
        Assert.Equal(NodeColor.Attention, Eval(Icmp(Severity.Normal), Snmp(Severity.Normal), Other(Severity.Critical), Other(Severity.Normal)));

    [Fact]
    public void Reboot_warning_on_uptime_is_yellow() =>
        Assert.Equal(NodeColor.Attention, Eval(Icmp(Severity.Normal), Snmp(Severity.Warning)));

    [Fact]
    public void Icmp_warning_is_yellow() =>
        Assert.Equal(NodeColor.Attention, Eval(Icmp(Severity.Warning), Snmp(Severity.Normal)));

    [Fact]
    public void Icmp_disabled_and_snmp_fine_but_a_monitor_critical_is_yellow() =>
        Assert.Equal(NodeColor.Attention, NodeColorRollup.Evaluate(true, false, true, [Snmp(Severity.Normal), Other(Severity.Critical)]));

    [Fact]
    public void Ranks_red_orange_yellow_green_gray()
    {
        NodeColor[] order = [NodeColor.Down, NodeColor.NoSnmp, NodeColor.Attention, NodeColor.Normal, NodeColor.NoData];
        for (var i = 0; i < order.Length - 1; i++)
        {
            Assert.True(order[i].Rank() > order[i + 1].Rank());
            Assert.Equal(order[i], order[i + 1].WorseOf(order[i]));
        }

        Assert.Equal(NodeColor.NoData, NodeColorExtensions.Worst([]));
    }

    [Theory]
    [InlineData(NodeColor.Down, "Down")]
    [InlineData(NodeColor.NoSnmp, "No SNMP")]
    [InlineData(NodeColor.Attention, "Needs attention")]
    [InlineData(NodeColor.Normal, "Normal")]
    [InlineData(NodeColor.NoData, "No data")]
    public void Labels(NodeColor color, string expected) => Assert.Equal(expected, color.Label());
}
