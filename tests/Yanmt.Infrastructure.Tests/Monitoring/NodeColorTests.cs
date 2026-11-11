using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yanmt.Core.Connectivity;
using Yanmt.Core.Monitoring;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Collectors.Snmp;
using Yanmt.Infrastructure.Credentials;
using Yanmt.Infrastructure.Inventory;
using Yanmt.Infrastructure.Monitoring;
using Yanmt.Infrastructure.Settings;
using Yanmt.Infrastructure.Tests.Collectors;
using static Yanmt.Infrastructure.Tests.Monitoring.MonitoringTestSupport;
using static Yanmt.Infrastructure.Tests.Monitoring.SnmpTestSupport;

namespace Yanmt.Infrastructure.Tests.Monitoring;

/// <summary>Node color from real StatusSource rows: the live cache, the NodeService DB fallback, and the SNMP recheck.</summary>
public class NodeColorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Thresholds T = new(80, 90);

    private static DefaultStatsReading Reading() => new(
        new SystemInfoReading("Cisco IOS XE Software", "1.3.6.1.4.1.9.1.2391", "lab-rtr-hub-01", "Lab rack 1", "noc@example.net", 8_640_000),
        new SnmpEngineInfo("800000090300AABBCCDD0001", 7, 1000),
        [10m],
        [new MemoryPool("Processor", 300, 700)]);

    private static IcmpPollResult Icmp(TestDatabase test, int node, Yanmt.Infrastructure.Collectors.Icmp.IcmpResult result)
    {
        test.Clock.Now = test.Clock.Now.AddMinutes(1);
        return new IcmpPollResult(node, test.Clock.GetUtcNow().UtcDateTime, new IcmpSettings(), result, null);
    }

    private static SnmpPollResult SnmpOk(TestDatabase test, int node)
    {
        test.Clock.Now = test.Clock.Now.AddSeconds(5);
        return new SnmpPollResult(node, test.Clock.GetUtcNow().UtcDateTime, T, T, Reading());
    }

    private static SnmpPollResult SnmpTimeout(TestDatabase test, int node)
    {
        test.Clock.Now = test.Clock.Now.AddSeconds(5);
        return new SnmpPollResult(node, test.Clock.GetUtcNow().UtcDateTime, T, T, null, FailureCategory.Timeout, "No SNMP response from 192.0.2.11:161.");
    }

    /// <summary>A node with ICMP and SNMP (profile assigned).</summary>
    private static async Task<int> BothNodeAsync(TestDatabase test)
    {
        var id = await test.AddNodeAsync("lab-rtr-hub-01", "192.0.2.11");
        var profile = await new CredentialService(test.Factory, test.Protector, test.Clock).CreateAsync(new CredentialEditModel
        {
            Name = "lab-v3", Kind = Core.Credentials.CredentialKind.SnmpV3, Username = "yanmt-ro", Port = 161,
            AuthPassword = "lab-auth-pass-01", PrivPassword = "lab-priv-pass-01",
        }, Ct);
        await using var db = test.Open();
        var node = await db.Nodes.SingleAsync(n => n.Id == id, Ct);
        node.SnmpEnabled = true;
        node.SnmpProfileId = profile.Id;
        await db.SaveChangesAsync(Ct);
        return id;
    }

    private static async Task AssertColorAsync(TestDatabase test, StatusCache cache, int node, NodeColor expected)
    {
        var svc = new NodeService(test.Factory, test.Clock);
        Assert.Equal(expected, cache.ColorOf(node));
        Assert.Equal(expected, (await svc.GetAsync(node, Ct))!.Color);
        Assert.Equal(expected, (await svc.ListAsync(Ct)).Single(n => n.Id == node).Color);
    }

    private static async Task GoCriticalAsync(TestDatabase test, StatusEngine engine, int node)
    {
        for (var i = 0; i < new IcmpSettings().FailuresBeforeCritical; i++)
        {
            await engine.ApplyAsync([Icmp(test, node, NoReply)], Ct);
        }
    }

    [Fact]
    public async Task Router_dies_red_without_yellow_first_and_snmp_recheck_is_requested_once()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await BothNodeAsync(test);
        var rechecks = new SnmpRecheckRequests();
        var (engine, cache, _) = test.Engine(rechecks: rechecks);

        await AssertColorAsync(test, cache, node, NodeColor.NoData);
        await engine.ApplyAsync([Icmp(test, node, Replies(2)), SnmpOk(test, node)], Ct);
        await AssertColorAsync(test, cache, node, NodeColor.Normal);

        await engine.ApplyAsync([Icmp(test, node, NoReply)], Ct);
        await AssertColorAsync(test, cache, node, NodeColor.Attention); // one lost burst: pending, yellow
        Assert.Empty(rechecks.Pending());

        await engine.ApplyAsync([Icmp(test, node, NoReply)], Ct);
        await engine.ApplyAsync([Icmp(test, node, NoReply)], Ct);
        // ICMP Critical; the last SNMP success was before that, so red, not "ping blocked".
        await AssertColorAsync(test, cache, node, NodeColor.Down);
        Assert.Equal([node], rechecks.Pending());
        rechecks.Done(node);

        await engine.ApplyAsync([SnmpTimeout(test, node)], Ct);
        await AssertColorAsync(test, cache, node, NodeColor.Down);

        await engine.ApplyAsync([Icmp(test, node, NoReply)], Ct); // still Critical: no new request
        Assert.Empty(rechecks.Pending());

        await engine.ApplyAsync([Icmp(test, node, Replies(2))], Ct);
        await AssertColorAsync(test, cache, node, NodeColor.NoSnmp); // back on ICMP, SNMP not yet
        await engine.ApplyAsync([SnmpOk(test, node)], Ct);
        await AssertColorAsync(test, cache, node, NodeColor.Normal);
    }

    [Fact]
    public async Task Ping_blocked_turns_yellow_once_snmp_answers()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await BothNodeAsync(test);
        var (engine, cache, _) = test.Engine();
        await engine.ApplyAsync([Icmp(test, node, Replies(2)), SnmpOk(test, node)], Ct);

        await GoCriticalAsync(test, engine, node);
        await AssertColorAsync(test, cache, node, NodeColor.Down);
        await engine.ApplyAsync([SnmpOk(test, node)], Ct); // the recheck answers
        await AssertColorAsync(test, cache, node, NodeColor.Attention);
    }

    [Fact]
    public async Task Snmp_down_while_ping_answers_is_orange()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await BothNodeAsync(test);
        var (engine, cache, _) = test.Engine();
        await engine.ApplyAsync([Icmp(test, node, Replies(2)), SnmpOk(test, node)], Ct);

        await engine.ApplyAsync([SnmpTimeout(test, node)], Ct);
        await AssertColorAsync(test, cache, node, NodeColor.NoSnmp);
        Assert.Equal(Severity.Unknown, cache.StateOf(node)); // the worst-severity rollup is unchanged
    }

    [Fact]
    public async Task Icmp_only_node_without_snmp_gets_no_recheck()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-spoke-01", "192.0.2.21");
        var rechecks = new SnmpRecheckRequests();
        var (engine, cache, _) = test.Engine(rechecks: rechecks);
        await engine.ApplyAsync([Icmp(test, node, Replies(2))], Ct);

        await GoCriticalAsync(test, engine, node);
        await AssertColorAsync(test, cache, node, NodeColor.Down);
        Assert.Empty(rechecks.Pending());
    }

    [Fact]
    public async Task Snmp_poller_runs_a_requested_recheck_out_of_cycle()
    {
        await using var test = await TestDatabase.CreateAsync();
        using var agent = new FakeSnmpAgent(Cred, CiscoDevice());
        var node = await test.AddSnmpNodeAsync(agent);

        var services = new ServiceCollection();
        services.AddSingleton(test.Factory);
        services.AddSingleton<TimeProvider>(test.Clock);
        services.AddSingleton(test.Protector);
        services.AddScoped<SettingsService>();
        services.AddScoped<CredentialService>();
        await using var provider = services.BuildServiceProvider();
        var rechecks = new SnmpRecheckRequests();
        var (engine, cache, _) = test.Engine(rechecks: rechecks);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var loop = engine.RunAsync(stop.Token);

        var poller = new SnmpPoller(provider.GetRequiredService<IServiceScopeFactory>(), engine, new DefaultStatsCollector(new SnmpV3Reader()),
            new InventoryChangeSignal(), Options.Create(new PollingOptions()), test.Clock, NullLogger<SnmpPoller>.Instance, rechecks);
        await poller.TickAsync(stop.Token); // first regular poll
        await StatusEngineTests.WaitUntilAsync(() => cache.Get(node)?.Sources.Count == 3 && poller.InFlightCount == 0);
        Assert.Equal(1, agent.GetRequestsAnswered);

        await poller.TickAsync(stop.Token); // the next regular poll is a full interval away
        Assert.Equal(1, agent.GetRequestsAnswered);

        rechecks.Request([node, 999]); // 999 isn't an SNMP target: dropped
        await poller.TickAsync(stop.Token);
        await StatusEngineTests.WaitUntilAsync(() => agent.GetRequestsAnswered == 2 && poller.InFlightCount == 0);
        Assert.Empty(rechecks.Pending());

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
        poller.Dispose();
    }
}
