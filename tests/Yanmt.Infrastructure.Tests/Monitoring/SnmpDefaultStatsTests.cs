using System.Net;
using Lextm.SharpSnmpLib;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yanmt.Core.Connectivity;
using Yanmt.Core.Monitoring;
using Yanmt.Core.Oids;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Collectors.Snmp;
using Yanmt.Infrastructure.Credentials;
using Yanmt.Infrastructure.Data.Entities;
using Yanmt.Infrastructure.Monitoring;
using Yanmt.Infrastructure.Settings;
using Yanmt.Infrastructure.Tests.Collectors;
using static Yanmt.Infrastructure.Tests.Monitoring.SnmpTestSupport;

namespace Yanmt.Infrastructure.Tests.Monitoring;

public class DefaultStatsCollectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SnmpTarget Target(FakeSnmpAgent agent) => new(IPAddress.Loopback, agent.EndPoint.Port, Cred, TimeSpan.FromSeconds(2));

    [Fact]
    public async Task Reads_system_group_cpu_and_hc_memory_in_three_round_trips()
    {
        using var agent = new FakeSnmpAgent(Cred, CiscoDevice(cpu: [12, 47]));

        var r = await new DefaultStatsCollector(new SnmpV3Reader()).CollectAsync(Target(agent), Ct);

        Assert.Equal("lab-rtr-hub-01.example.net", r.System.Name);
        Assert.Equal(("Lab rack 1", "noc@example.net", 8_640_000u), (r.System.Location, r.System.Contact, r.System.UptimeTicks!.Value));
        Assert.StartsWith("Cisco IOS XE Software", r.System.Descr);
        Assert.Equal("1.3.6.1.4.1.9.1.2391", r.System.ObjectId);
        Assert.Equal([12m, 47m], r.CpuFiveMinute);
        Assert.Equal(("Processor", 30.0), (DefaultStats.PickMemory(r.MemoryPools)!.Pool, DefaultStats.PickMemory(r.MemoryPools)!.Percent));
        Assert.Equal(FakeSnmpAgent.Boots, r.Engine.Boots);

        // Discovery, the system GET, and one GETBULK for six columns (CPU + mempool).
        Assert.Equal((1, 1, 1), (agent.DiscoveriesAnswered, agent.GetRequestsAnswered, agent.BulkRequestsAnswered));
    }

    [Fact]
    public async Task Falls_back_to_cisco_memory_pool_mib()
    {
        using var agent = new FakeSnmpAgent(Cred, CiscoDevice(enhancedMemory: false, legacyMemory: true));

        var r = await new DefaultStatsCollector(new SnmpV3Reader()).CollectAsync(Target(agent), Ct);

        Assert.Equal(90.0, DefaultStats.PickMemory(r.MemoryPools)!.Percent);
        Assert.Equal(2, agent.BulkRequestsAnswered);
    }

    [Fact]
    public async Task A_device_without_the_cisco_mibs_reads_empty_cpu_and_memory()
    {
        using var agent = new FakeSnmpAgent(Cred, CiscoDevice(cpu: [], enhancedMemory: false));

        var r = await new DefaultStatsCollector(new SnmpV3Reader()).CollectAsync(Target(agent), Ct);

        Assert.Empty(r.CpuFiveMinute);
        Assert.Null(DefaultStats.PickMemory(r.MemoryPools));
        Assert.NotNull(r.System.UptimeTicks);
    }
}

public class StatusEngineSnmpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Thresholds T = new(80, 90);
    private const string Engine = "800000090300AABBCCDD0001";

    private static DefaultStatsReading Reading(uint uptime = 8_640_000, decimal[]? cpu = null, MemoryPool[]? memory = null, int boots = 7) => new(
        new SystemInfoReading("Cisco IOS XE Software", "1.3.6.1.4.1.9.1.2391", "lab-rtr-hub-01", "Lab rack 1", "noc@example.net", uptime),
        new SnmpEngineInfo(Engine, boots, 1000),
        cpu ?? [10m],
        memory ?? [new MemoryPool("Processor", 300, 700)]);

    private static SnmpPollResult Ok(TestDatabase test, int node, DefaultStatsReading reading)
    {
        test.Clock.Now = test.Clock.Now.AddMinutes(5);
        return new SnmpPollResult(node, test.Clock.GetUtcNow().UtcDateTime, T, T, reading);
    }

    private static SnmpPollResult Failed(TestDatabase test, int node, FailureCategory category = FailureCategory.Timeout)
    {
        test.Clock.Now = test.Clock.Now.AddMinutes(5);
        return new SnmpPollResult(node, test.Clock.GetUtcNow().UtcDateTime, T, T, null, category, "No SNMP response from 192.0.2.11:161.");
    }

    private static async Task<int> SnmpNodeAsync(TestDatabase test)
    {
        var id = await test.AddNodeAsync("lab-rtr-hub-01", "192.0.2.11", icmp: false);
        await using var db = test.Open();
        var credentials = new CredentialService(test.Factory, test.Protector, test.Clock);
        var profile = await credentials.CreateAsync(new CredentialEditModel
        {
            Name = "lab-v3", Kind = Core.Credentials.CredentialKind.SnmpV3, Username = "yanmt-ro", Port = 161,
            AuthPassword = "lab-auth-pass-01", PrivPassword = "lab-priv-pass-01",
        }, Ct);
        var node = await db.Nodes.SingleAsync(n => n.Id == id, Ct);
        node.SnmpEnabled = true;
        node.SnmpProfileId = profile.Id;
        await db.SaveChangesAsync(Ct);
        return id;
    }

    private static async Task<List<Event>> EventsAsync(TestDatabase test)
    {
        await using var db = test.Open();
        return await db.Events.OrderBy(e => e.Id).ToListAsync(Ct);
    }

    [Fact]
    public async Task First_poll_creates_sources_samples_and_system_info_without_events()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await SnmpNodeAsync(test);
        var (engine, cache, _) = test.Engine();

        await engine.ApplyAsync([Ok(test, node, Reading(cpu: [42m]))], Ct);

        var status = cache.Get(node)!;
        Assert.Equal(Severity.Normal, status.State);
        Assert.Equal("42%", status.Source(StatusSourceType.Cpu)!.ValueText);
        Assert.Equal("30%", status.Source(StatusSourceType.Memory)!.ValueText);
        Assert.Equal("1d 0h 0m", status.Source(StatusSourceType.Uptime)!.ValueText);
        Assert.Empty(await EventsAsync(test));

        await using var db = test.Open();
        Assert.Equal(2, await db.MetricSamples.CountAsync(Ct));
        var info = await db.NodeSystemInfo.SingleAsync(Ct);
        Assert.Equal(("lab-rtr-hub-01", "Lab rack 1", Engine, 7, "Processor"), (info.SysName, info.SysLocation, info.EngineId, info.EngineBoots!.Value, info.MemoryPool));
        Assert.Equal(8_640_000, info.UptimeTicks);
    }

    [Fact]
    public async Task Cpu_and_memory_thresholds_raise_events()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await SnmpNodeAsync(test);
        var (engine, cache, _) = test.Engine();

        await engine.ApplyAsync([Ok(test, node, Reading(cpu: [10m]))], Ct);
        await engine.ApplyAsync([Ok(test, node, Reading(cpu: [85m], uptime: 8_670_000))], Ct);
        Assert.Equal(Severity.Warning, cache.StateOf(node));
        await engine.ApplyAsync([Ok(test, node, Reading(cpu: [95m], uptime: 8_700_000, memory: [new MemoryPool("Processor", 950, 50)]))], Ct);
        Assert.Equal(Severity.Critical, cache.StateOf(node));

        var events = await EventsAsync(test);
        Assert.Equal(
            [(StatusSourceType.Cpu, Severity.Warning), (StatusSourceType.Cpu, Severity.Critical), (StatusSourceType.Memory, Severity.Critical)],
            events.Select(e => (e.Source, e.NewState)));
        Assert.Contains("CPU 95% (5-minute average) (Critical at 90%)", events[1].Message);
        Assert.Contains("Processor pool 95% used", events[2].Message);
    }

    [Fact]
    public async Task A_failed_poll_is_one_event_and_unknown_until_it_recovers()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await SnmpNodeAsync(test);
        var (engine, cache, _) = test.Engine();
        await engine.ApplyAsync([Ok(test, node, Reading())], Ct);

        await engine.ApplyAsync([Failed(test, node)], Ct);
        var status = cache.Get(node)!;
        Assert.Equal(Severity.Unknown, status.State);
        Assert.Equal("SNMP timeout", status.Source(StatusSourceType.Uptime)!.ValueText);
        Assert.All(status.Sources, s => Assert.Equal(Severity.Unknown, s.State));

        await engine.ApplyAsync([Failed(test, node)], Ct); // still failing: no second event
        await engine.ApplyAsync([Ok(test, node, Reading(uptime: 8_700_000))], Ct);
        Assert.Equal(Severity.Normal, cache.StateOf(node));

        var events = await EventsAsync(test);
        Assert.Equal([Severity.Unknown, Severity.Normal], events.Select(e => e.NewState));
        Assert.All(events, e => Assert.Equal(StatusSourceType.Uptime, e.Source));
        Assert.Contains("SNMP poll failed (timeout)", events[0].Message);
        Assert.Contains("SNMP polling recovered", events[1].Message);
    }

    [Fact]
    public async Task Reboot_shows_warning_for_one_poll()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await SnmpNodeAsync(test);
        var (engine, cache, _) = test.Engine();
        await engine.ApplyAsync([Ok(test, node, Reading(uptime: 8_640_000))], Ct);

        await engine.ApplyAsync([Ok(test, node, Reading(uptime: 12_000, boots: 8))], Ct);
        Assert.Equal(Severity.Warning, cache.StateOf(node));
        Assert.Equal("2m 0s", cache.Get(node)!.Source(StatusSourceType.Uptime)!.ValueText);

        await engine.ApplyAsync([Ok(test, node, Reading(uptime: 42_000, boots: 8))], Ct);
        Assert.Equal(Severity.Normal, cache.StateOf(node));

        var reboot = Assert.Single(await EventsAsync(test)); // the expiry back to Normal isn't an event
        Assert.Equal((StatusSourceType.Uptime, Severity.Warning), (reboot.Source, reboot.NewState));
        Assert.Contains("rebooted", reboot.Message);
        await using var db = test.Open();
        Assert.NotNull((await db.NodeSystemInfo.SingleAsync(Ct)).LastRebootUtc);
    }

    [Fact]
    public async Task Missing_cpu_or_memory_mib_leaves_that_source_out_of_the_rollup()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await SnmpNodeAsync(test);
        var (engine, cache, _) = test.Engine();

        await engine.ApplyAsync([Ok(test, node, Reading(cpu: [], memory: []))], Ct);

        var status = cache.Get(node)!;
        Assert.Equal(Severity.Normal, status.State); // not graying the node forever
        Assert.Equal([StatusSourceType.Uptime], status.Sources.Select(s => s.Type));
        await using var db = test.Open();
        var cpu = await db.StatusSources.SingleAsync(s => s.SourceType == StatusSourceType.Cpu, Ct);
        Assert.Equal((false, "CPU MIB not available"), (cpu.Enabled, cpu.ValueText));
    }

    [Fact]
    public async Task Turning_snmp_off_disables_its_sources_and_only_a_poll_turns_them_back_on()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await SnmpNodeAsync(test);
        var (engine, cache, _) = test.Engine();
        await engine.ApplyAsync([Ok(test, node, Reading())], Ct);

        await using (var db = test.Open())
        {
            (await db.Nodes.SingleAsync(Ct)).SnmpEnabled = false;
            await db.SaveChangesAsync(Ct);
        }

        await engine.SyncAsync(Ct);
        Assert.Empty(cache.Get(node)!.Sources);

        // A poll result that was already in flight is ignored.
        await engine.ApplyAsync([Ok(test, node, Reading())], Ct);
        Assert.Empty(cache.Get(node)!.Sources);

        await using (var db = test.Open())
        {
            (await db.Nodes.SingleAsync(Ct)).SnmpEnabled = true;
            await db.SaveChangesAsync(Ct);
        }

        await engine.SyncAsync(Ct);
        Assert.Empty(cache.Get(node)!.Sources); // not back until polled
        await engine.ApplyAsync([Ok(test, node, Reading())], Ct);
        Assert.Equal(3, cache.Get(node)!.Sources.Count);
        Assert.Empty(await EventsAsync(test));
    }
}

public class SnmpPollerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Polls_snmp_nodes_through_their_profile_end_to_end()
    {
        await using var test = await TestDatabase.CreateAsync();
        using var agent = new FakeSnmpAgent(Cred, CiscoDevice(cpu: [33]));
        var node = await test.AddSnmpNodeAsync(agent);
        await test.AddNodeAsync("lab-rtr-noprofile-01", "192.0.2.50", icmp: false); // SNMP off: not polled

        var services = new ServiceCollection();
        services.AddSingleton(test.Factory);
        services.AddSingleton<TimeProvider>(test.Clock);
        services.AddSingleton(test.Protector);
        services.AddScoped<SettingsService>();
        services.AddScoped<CredentialService>();
        await using var provider = services.BuildServiceProvider();
        var (engine, cache, _) = test.Engine();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var loop = engine.RunAsync(stop.Token);

        var poller = new SnmpPoller(provider.GetRequiredService<IServiceScopeFactory>(), engine, new DefaultStatsCollector(new SnmpV3Reader()),
            new InventoryChangeSignal(), Options.Create(new PollingOptions()), test.Clock, NullLogger<SnmpPoller>.Instance);
        await poller.TickAsync(stop.Token);

        await StatusEngineTests.WaitUntilAsync(() => cache.Get(node)?.Sources.Count == 3);
        Assert.Equal("33%", cache.Get(node)!.Source(StatusSourceType.Cpu)!.ValueText);
        Assert.Equal(1, agent.GetRequestsAnswered);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
        poller.Dispose();
    }
}

public class NodeSnmpToolsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (InterfaceService Interfaces, SnmpQueryService Query, SnmpTargetResolver Targets) Services(TestDatabase test)
    {
        var resolver = new SnmpTargetResolver(test.Factory, new CredentialService(test.Factory, test.Protector, test.Clock),
            new SettingsService(test.Factory, test.Clock));
        var reader = new SnmpV3Reader();
        return (new InterfaceService(resolver, reader, TimeProvider.System), new SnmpQueryService(resolver, reader, BuiltInOidNames.Instance, TimeProvider.System), resolver);
    }

    [Fact]
    public async Task Interfaces_are_listed_with_rates_from_two_samples()
    {
        await using var test = await TestDatabase.CreateAsync();
        using var agent = new FakeSnmpAgent(Cred, CiscoDevice());
        var node = await test.AddSnmpNodeAsync(agent);
        var (interfaces, _, _) = Services(test);

        var reading = interfaces.ReadAsync(node, TimeSpan.FromSeconds(1), null, Ct);
        await Task.Delay(300, Ct);
        agent.Set($"{IfHcInOctets}.1", new Counter64(1_000_000 + 125_000)); // +1 Mb over ~1 s
        agent.Set($"{IfHcOutOctets}.1", new Counter64(2_000_000 + 12_500));
        var table = await reading;

        Assert.Null(table.Error);
        Assert.Equal(["1", "2", "3"], table.Rows.Select(r => r.Index));
        var gi1 = table.Rows[0];
        Assert.Equal(("Gi1", "GigabitEthernet1", "uplink to lab-rtr-spoke-01", 1, 1, 1000.0), (gi1.Name, gi1.Description, gi1.Alias, gi1.AdminStatus!.Value, gi1.OperStatus!.Value, gi1.SpeedMbps!.Value));
        Assert.InRange(gi1.InBps!.Value, 1_000_000 / 1.5, 1_000_000 * 1.05);
        Assert.InRange(gi1.OutBps!.Value, 100_000 / 1.5, 100_000 * 1.05);
        Assert.InRange(gi1.InUtilization!.Value, 0.06, 0.11);
        Assert.Equal(0, table.Rows[1].InBps);
        Assert.Equal(("down", "down"), (InterfaceRow.Status(table.Rows[2].AdminStatus), InterfaceRow.Status(table.Rows[2].OperStatus)));
    }

    [Fact]
    public async Task Query_tool_gets_and_walks_by_name()
    {
        await using var test = await TestDatabase.CreateAsync();
        using var agent = new FakeSnmpAgent(Cred, CiscoDevice());
        var node = await test.AddSnmpNodeAsync(agent);
        var (_, query, _) = Services(test);

        var get = await query.QueryAsync(node, SnmpQueryOperation.Get, "sysName.0", Ct);
        Assert.Null(get.Error);
        Assert.Equal(("1.3.6.1.2.1.1.5.0", "sysName.0", "lab-rtr-hub-01.example.net"), (get.Rows[0].Oid, get.Rows[0].Name, get.Rows[0].Value));

        var walk = await query.QueryAsync(node, SnmpQueryOperation.Walk, "ifName", Ct);
        Assert.Equal(["ifName.1", "ifName.2", "ifName.3"], walk.Rows.Select(r => r.Name));
        Assert.False(walk.Truncated);

        var next = await query.QueryAsync(node, SnmpQueryOperation.GetNext, "sysUpTime", Ct);
        Assert.Equal("sysUpTime.0", Assert.Single(next.Rows).Name);
    }

    [Fact]
    public async Task Query_tool_explains_bad_input_and_missing_configuration()
    {
        await using var test = await TestDatabase.CreateAsync();
        var bare = await test.AddNodeAsync("lab-rtr-bare-01", "192.0.2.60");
        var (interfaces, query, _) = Services(test);

        Assert.Contains("isn't a numeric OID or a known object name", (await query.QueryAsync(bare, SnmpQueryOperation.Get, "noSuchThing", Ct)).Error);
        Assert.Contains("No SNMPv3 profile", (await query.QueryAsync(bare, SnmpQueryOperation.Get, "sysName.0", Ct)).Error);
        Assert.Contains("No SNMPv3 profile", (await interfaces.ReadAsync(bare, TimeSpan.Zero, null, Ct)).Error);
    }
}

public class NodeDetailServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Charts_downsample_icmp_and_metric_samples_for_the_range()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-hub-01", "192.0.2.11");
        var now = test.Clock.GetUtcNow().UtcDateTime;
        await using (var db = test.Open())
        {
            var cpu = new StatusSource { NodeId = node, SourceType = StatusSourceType.Cpu, SinceUtc = now };
            db.StatusSources.Add(cpu);
            for (var i = 0; i < 120; i++)
            {
                db.IcmpSamples.Add(new IcmpSample
                {
                    NodeId = node, TimestampUtc = now.AddMinutes(-i), Sent = 4, Received = i == 5 ? 0 : 4,
                    AvgMs = i == 5 ? null : 10 + i % 3, LossPercent = i == 5 ? 100 : 0,
                });
            }

            for (var i = 0; i < 12; i++)
            {
                db.MetricSamples.Add(new MetricSample { StatusSource = cpu, TimestampUtc = now.AddMinutes(-5 * i), Value = 20 + i });
            }

            db.IcmpSamples.Add(new IcmpSample { NodeId = node, TimestampUtc = now.AddDays(-2), Sent = 4, Received = 4, AvgMs = 999 }); // outside 1 h
            await db.SaveChangesAsync(Ct);
        }

        var charts = await new NodeDetailService(test.Factory, new SettingsService(test.Factory, test.Clock), test.Clock)
            .GetChartsAsync(node, ChartRange.Hour, Ct);

        Assert.InRange(charts.Latency.Count, 55, 61); // one sample a minute for the last hour, one was a total loss
        Assert.All(charts.Latency, b => Assert.InRange(b.Avg, 10, 12));
        Assert.Equal(100, charts.Loss.Max(b => b.Max));
        Assert.Equal(12, charts.Cpu.Count);
        Assert.Empty(charts.Memory);
    }

    [Fact]
    public async Task Long_ranges_read_rollups_and_raw_samples_after_the_last_rolled_hour()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-hub-01", "192.0.2.11");
        var now = test.Clock.GetUtcNow().UtcDateTime;
        var lastHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(-3);
        await using (var db = test.Open())
        {
            var cpu = new StatusSource { NodeId = node, SourceType = StatusSourceType.Cpu, SinceUtc = now };
            db.StatusSources.Add(cpu);
            // Rollups only 20 days back; raw samples exist for the same hour (must not double count) and after it.
            for (var d = 20; d >= 0; d--)
            {
                var hour = lastHour.AddDays(-d);
                db.MetricRollupHours.Add(new MetricRollupHour { StatusSource = cpu, HourUtc = hour, Min = 10, Avg = 20, Max = 30, SampleCount = 12 });
                db.IcmpRollupHours.Add(new IcmpRollupHour { NodeId = node, HourUtc = hour, Sent = 240, Received = 238, MinMs = 1, AvgMs = 5, MaxMs = 9, LossPercent = 50, SampleCount = 60 });
            }

            db.MetricSamples.Add(new MetricSample { StatusSource = cpu, TimestampUtc = lastHour.AddMinutes(5), Value = 99 }); // already rolled up
            db.MetricSamples.Add(new MetricSample { StatusSource = cpu, TimestampUtc = now.AddMinutes(-10), Value = 60 });
            await db.SaveChangesAsync(Ct);
        }

        var service = new NodeDetailService(test.Factory, new SettingsService(test.Factory, test.Clock), test.Clock);
        var month = await service.GetChartsAsync(node, ChartRange.Month, Ct);

        Assert.Equal(30, month.Cpu.Max(b => b.Max)); // the raw 99 inside a rolled hour is ignored
        Assert.Equal(60, month.Cpu[^1].Avg); // raw tail after the last rolled hour
        Assert.Equal(50, month.Loss.Max(b => b.Max));
        Assert.Equal(5, month.Latency[0].Avg);
        Assert.True(month.FromUtc <= now.AddDays(-30));

        var week = await service.GetChartsAsync(node, ChartRange.Week, Ct);
        Assert.Equal(99, week.Cpu.Max(b => b.Max)); // short ranges stay on raw samples
        Assert.Empty(week.Latency);
    }

    [Fact]
    public async Task Effective_thresholds_combine_node_overrides_with_settings()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-hub-01", "192.0.2.11");
        await using (var db = test.Open())
        {
            (await db.Nodes.SingleAsync(Ct)).CpuWarningPercent = 60;
            await db.SaveChangesAsync(Ct);
        }

        var settings = new SettingsService(test.Factory, test.Clock);
        await settings.SetAsync(SettingKeys.Snmp, new SnmpSettings { CpuCriticalPercent = 95, MemoryWarningPercent = 70 }, Ct);

        var (cpu, memory, interval, _) = await new NodeDetailService(test.Factory, settings, test.Clock).GetPollingAsync(node, Ct);

        Assert.Equal(new Thresholds(60, 95), cpu);
        Assert.Equal(new Thresholds(70, 90), memory);
        Assert.Equal(300, interval);
    }
}
