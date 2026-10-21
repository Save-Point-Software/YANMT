using System.Net;
using Lextm.SharpSnmpLib;
using Yanmt.Core.Connectivity;
using Yanmt.Core.Credentials;
using Yanmt.Infrastructure.Collectors.Snmp;
using Yanmt.Infrastructure.Credentials;

namespace Yanmt.Infrastructure.Tests.Collectors;

/// <summary>GETNEXT, GETBULK, multi-column WALK, and the engine-discovery cache.</summary>
public class SnmpV3ReaderOperationsTests
{
    private const string IfDescr = "1.3.6.1.2.1.2.2.1.2";
    private const string IfOperStatus = "1.3.6.1.2.1.2.2.1.8";
    private const string SysDescr = "1.3.6.1.2.1.1.1.0";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SnmpV3Credential Cred = new("yanmt-ro", SnmpAuthProtocol.Sha256, "auth-pass-0001", SnmpPrivProtocol.Aes128, "priv-pass-0001");

    /// <summary>A three-interface ifTable slice, plus objects either side of it.</summary>
    private static Dictionary<string, ISnmpData> Table(int interfaces = 3)
    {
        var t = new Dictionary<string, ISnmpData>
        {
            [SysDescr] = new OctetString("Cisco IOS XE Software"),
            ["1.3.6.1.2.1.2.1.0"] = new Integer32(interfaces), // ifNumber, just before the table
            ["1.3.6.1.2.1.31.1.1.1.1.1"] = new OctetString("Gi1"), // ifXTable, after it
        };
        for (var i = 1; i <= interfaces; i++)
        {
            t[$"{IfDescr}.{i}"] = new OctetString($"GigabitEthernet{i}");
            t[$"{IfOperStatus}.{i}"] = new Integer32(i == 2 ? 2 : 1);
        }

        return t;
    }

    private static SnmpTarget Target(FakeSnmpAgent agent, double timeoutSeconds = 2) =>
        new(IPAddress.Loopback, agent.EndPoint.Port, Cred, TimeSpan.FromSeconds(timeoutSeconds));

    [Fact]
    public async Task GetNext_returns_the_following_object()
    {
        using var agent = new FakeSnmpAgent(Cred, Table());

        var result = await new SnmpV3Reader().GetNextAsync(Target(agent), [IfDescr, $"{IfDescr}.3"], Ct);

        Assert.Equal([$"{IfDescr}.1", $"{IfOperStatus}.1"], result.Values.Select(v => v.Oid));
        Assert.Equal("GigabitEthernet1", result.Values[0].Text);
        Assert.Equal(1, agent.NextRequestsAnswered);
    }

    [Fact]
    public async Task GetBulk_interleaves_repetitions()
    {
        using var agent = new FakeSnmpAgent(Cred, Table());

        var result = await new SnmpV3Reader().GetBulkAsync(Target(agent), [IfDescr, IfOperStatus], 2, Ct);

        Assert.Equal([$"{IfDescr}.1", $"{IfOperStatus}.1", $"{IfDescr}.2", $"{IfOperStatus}.2"], result.Values.Select(v => v.Oid));
        Assert.Equal(2m, result.Values[3].Number);
    }

    [Fact]
    public async Task Walk_reads_table_columns_side_by_side_and_stops_at_each_subtree_end()
    {
        using var agent = new FakeSnmpAgent(Cred, Table(interfaces: 60));

        var walk = await new SnmpV3Reader().WalkAsync(Target(agent), [IfDescr, IfOperStatus], 1000, Ct);

        Assert.Equal(60, walk[IfDescr].Values.Count);
        Assert.Equal(60, walk[IfOperStatus].Values.Count);
        Assert.All(walk.Columns, c => Assert.False(c.Truncated));
        Assert.Equal("GigabitEthernet60", walk[IfDescr].Values[^1].Text);
        Assert.All(walk[IfOperStatus].Values, v => Assert.StartsWith(IfOperStatus + ".", v.Oid));

        // 2 columns share a 48-varbind budget: 24 rows per request, so 60 rows take 3 requests.
        Assert.Equal(3, walk.Requests);
        Assert.Equal(3, agent.BulkRequestsAnswered);
    }

    [Fact]
    public async Task Walk_stops_at_end_of_mib_and_on_an_empty_subtree()
    {
        using var agent = new FakeSnmpAgent(Cred, Table());

        var walk = await new SnmpV3Reader().WalkAsync(Target(agent), ["1.3.6.1.2.1.31.1.1.1.1", "1.3.6.1.4.1.9.9.109"], 100, Ct);

        Assert.Equal("Gi1", Assert.Single(walk["1.3.6.1.2.1.31.1.1.1.1"].Values).Text); // last object in the MIB
        Assert.Empty(walk["1.3.6.1.4.1.9.9.109"].Values); // nothing under it: the next object is outside
    }

    [Fact]
    public async Task Walk_honours_the_row_limit_and_marks_truncation()
    {
        using var agent = new FakeSnmpAgent(Cred, Table(interfaces: 30));

        var walk = await new SnmpV3Reader().WalkAsync(Target(agent), [IfDescr], 10, Ct);

        Assert.Equal(10, walk[IfDescr].Values.Count);
        Assert.True(walk[IfDescr].Truncated);
    }

    [Fact]
    public async Task Walk_copes_with_an_agent_that_truncates_bulk_replies()
    {
        using var agent = new FakeSnmpAgent(Cred, Table(interfaces: 20)) { MaxBulkVarbinds = 3 };

        var walk = await new SnmpV3Reader().WalkAsync(Target(agent), [IfDescr, IfOperStatus], 1000, Ct);

        Assert.Equal(20, walk[IfDescr].Values.Count);
        Assert.Equal(20, walk[IfOperStatus].Values.Count);
    }

    [Fact]
    public async Task Engine_discovery_is_cached_between_requests()
    {
        using var agent = new FakeSnmpAgent(Cred, Table());
        var reader = new SnmpV3Reader();

        await reader.GetAsync(Target(agent), [SysDescr], Ct);
        await reader.GetAsync(Target(agent), [SysDescr], Ct);
        await reader.WalkAsync(Target(agent), [IfDescr], 100, Ct);

        Assert.Equal(1, agent.DiscoveriesAnswered);
        Assert.Equal(2, agent.GetRequestsAnswered);
    }

    [Fact]
    public async Task Agent_reboot_is_handled_through_not_in_time_window_and_reported_in_the_engine_info()
    {
        using var agent = new FakeSnmpAgent(Cred, Table());
        var reader = new SnmpV3Reader();
        var before = await reader.GetAsync(Target(agent), [SysDescr], Ct);

        agent.CurrentBoots = FakeSnmpAgent.Boots + 1; // the device rebooted
        var after = await reader.GetAsync(Target(agent), [SysDescr], Ct);

        Assert.Equal(FakeSnmpAgent.Boots, before.Engine.Boots);
        Assert.Equal(FakeSnmpAgent.Boots + 1, after.Engine.Boots);
        Assert.Equal(1, agent.DiscoveriesAnswered); // the report carried the new boots; no rediscovery needed
    }

    [Fact]
    public async Task A_device_that_dies_after_caching_gets_a_plain_timeout_not_a_privacy_hint()
    {
        using var agent = new FakeSnmpAgent(Cred, Table());
        var reader = new SnmpV3Reader();
        await reader.GetAsync(Target(agent), [SysDescr], Ct);

        agent.Silent = true;
        var ex = await Assert.ThrowsAsync<SnmpQueryException>(() => reader.GetAsync(Target(agent, 0.5), [SysDescr], Ct));

        Assert.Equal(FailureCategory.Timeout, ex.Category);
        Assert.Contains("Check reachability", ex.Message);
        Assert.DoesNotContain("privacy", ex.Message);
    }

    [Fact]
    public async Task Wrong_privacy_password_with_a_cached_engine_still_gets_the_privacy_hint()
    {
        using var agent = new FakeSnmpAgent(Cred, Table());
        var reader = new SnmpV3Reader();
        await reader.GetAsync(Target(agent), [SysDescr], Ct); // caches the engine with the right credential

        agent.SilentAfterDiscovery = true; // as IOS-XE does for a wrong privacy password
        var ex = await Assert.ThrowsAsync<SnmpQueryException>(() => reader.GetAsync(Target(agent, 0.5), [SysDescr], Ct));

        Assert.Equal(FailureCategory.Timeout, ex.Category);
        Assert.Contains("privacy password", ex.Message);
        Assert.Equal(2, agent.DiscoveriesAnswered); // the timeout on the cached engine triggered a fresh discovery
    }

    [Fact]
    public async Task Numbers_are_exact_for_counters_gauges_and_timeticks()
    {
        using var agent = new FakeSnmpAgent(Cred, new Dictionary<string, ISnmpData>
        {
            ["1.3.6.1.2.1.31.1.1.1.6.1"] = new Counter64(ulong.MaxValue),
            ["1.3.6.1.2.1.2.2.1.10.1"] = new Counter32(uint.MaxValue),
            ["1.3.6.1.2.1.1.3.0"] = new TimeTicks(123456),
            ["1.3.6.1.2.1.2.2.1.5.1"] = new Gauge32(1_000_000_000),
            ["1.3.6.1.2.1.1.5.0"] = new OctetString("lab-rtr-hub-01"),
        });

        var r = await new SnmpV3Reader().GetAsync(Target(agent),
            ["1.3.6.1.2.1.31.1.1.1.6.1", "1.3.6.1.2.1.2.2.1.10.1", "1.3.6.1.2.1.1.3.0", "1.3.6.1.2.1.2.2.1.5.1", "1.3.6.1.2.1.1.5.0"], Ct);

        Assert.Equal([18446744073709551615m, 4294967295m, 123456m, 1000000000m, null], r.Values.Select(v => v.Number));
    }
}
