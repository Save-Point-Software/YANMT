using Lextm.SharpSnmpLib;
using Microsoft.EntityFrameworkCore;
using Yanmt.Core.Monitors;
using Yanmt.Core.Oids;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Collectors.Snmp;
using Yanmt.Infrastructure.Credentials;
using Yanmt.Infrastructure.Data.Entities;
using Yanmt.Infrastructure.Maps;
using Yanmt.Infrastructure.Monitoring;
using Yanmt.Infrastructure.Monitors;
using Yanmt.Infrastructure.Settings;
using Yanmt.Infrastructure.Tests.Collectors;
using Yanmt.Infrastructure.Tests.Monitoring;
using static Yanmt.Infrastructure.Tests.Monitoring.SnmpTestSupport;

namespace Yanmt.Infrastructure.Tests.Monitors;

internal static class MonitorTestSupport
{
    public static MonitorService Service(this TestDatabase test, InventoryChangeSignal? signal = null) =>
        new(test.Factory, BuiltInOidNames.Instance, test.Clock, signal);

    public static MonitorEditModel OperStatus(string name = "If oper status", bool filterAdminUp = true) => new()
    {
        Name = name,
        OidMode = OidMode.Table,
        Oid = "ifOperStatus",
        LabelOid = "ifDescr",
        ValueMap = "1=up, 2=down",
        Rules = [new RuleRow { Operator = RuleOperator.Equal, Value = "up", State = Severity.Normal }],
        DefaultState = Severity.Critical,
        FilterOid = filterAdminUp ? "ifAdminStatus" : null,
        FilterOperator = RuleOperator.Equal,
        FilterValue = filterAdminUp ? "1" : null,
    };

    public static async Task<int> CreateOkAsync(this MonitorService svc, MonitorEditModel model)
    {
        var r = await svc.CreateAsync(model, TestContext.Current.CancellationToken);
        Assert.True(r.Ok, r.Error);
        return r.Id!.Value;
    }

    public static async Task TagAsync(this TestDatabase test, int nodeId, string tag)
    {
        await using var db = test.Open();
        var t = await db.Tags.FirstOrDefaultAsync(x => x.Name == tag, TestContext.Current.CancellationToken) ?? new Tag { Name = tag };
        db.NodeTags.Add(new NodeTag { NodeId = nodeId, Tag = t });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Plans, polls the fake agent and applies the results, as the poller would, at the test clock's time.</summary>
    public static async Task PollAsync(this TestDatabase test, StatusEngine engine, int nodeId, FakeSnmpAgent agent)
    {
        var ct = TestContext.Current.CancellationToken;
        Dictionary<int, List<MonitorWork>> plan;
        await using (var db = test.Open())
        {
            plan = await MonitorPlanner.PlanAsync(db, ct);
        }

        var works = plan.GetValueOrDefault(nodeId) ?? [];
        var target = new SnmpTarget(System.Net.IPAddress.Loopback, agent.EndPoint.Port, Cred, TimeSpan.FromSeconds(2));
        var reading = await new MonitorCollector(new SnmpV3Reader()).CollectAsync(target, works, ct);
        await engine.ApplyAsync([new MonitorPollResult(nodeId, test.Clock.GetUtcNow().UtcDateTime, works, reading)], ct);
    }

    public static IReadOnlyList<SourceStatus> Custom(this StatusCache cache, int nodeId) =>
        cache.Get(nodeId)?.Sources.Where(s => s.Type == StatusSourceType.Custom).ToList() ?? [];
}

public class MonitorServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Names_become_numeric_oids_and_everything_is_audited()
    {
        await using var test = await TestDatabase.CreateAsync();
        var signal = new InventoryChangeSignal();
        var svc = test.Service(signal);

        var id = await svc.CreateOkAsync(MonitorTestSupport.OperStatus());

        var (model, _) = (await svc.GetAsync(id, Ct))!.Value;
        Assert.Equal(("1.3.6.1.2.1.2.2.1.8", "1.3.6.1.2.1.2.2.1.2", "1.3.6.1.2.1.2.2.1.7"), (model.Oid, model.LabelOid, model.FilterOid));
        Assert.Equal("1=up, 2=down", model.ValueMap);
        Assert.Equal(1, signal.Version);
        await using var db = test.Open();
        Assert.Contains(await db.AuditEntries.ToListAsync(Ct), a => a.EntityType == "MonitorDefinition" && a.Action == "Create");
    }

    [Fact]
    public async Task Invalid_definitions_are_refused_with_the_field()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = test.Service();
        var bad = MonitorTestSupport.OperStatus();
        bad.Oid = "noSuchObject";

        var r = await svc.CreateAsync(bad, Ct);

        Assert.False(r.Ok);
        Assert.Equal("Oid", r.Field);

        var rule = MonitorTestSupport.OperStatus();
        rule.Rules.Add(new RuleRow { Operator = RuleOperator.Greater, Value = "fast", State = Severity.Warning });
        Assert.Equal("Rules", (await svc.CreateAsync(rule, Ct)).Field);
    }

    [Fact]
    public async Task Names_are_unique_and_copies_get_a_free_name_and_start_disabled()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = test.Service();
        var id = await svc.CreateOkAsync(MonitorTestSupport.OperStatus());

        Assert.False((await svc.CreateAsync(MonitorTestSupport.OperStatus("IF OPER STATUS"), Ct)).Ok);
        var copy = (await svc.CopyAsync(id, Ct)).Id!.Value;
        var copy2 = (await svc.CopyAsync(id, Ct)).Id!.Value;

        var list = await svc.ListAsync(Ct);
        Assert.Equal(["Copy 2 of If oper status", "Copy of If oper status", "If oper status"], list.Select(m => m.Name));
        Assert.False(list.Single(m => m.Id == copy).Enabled);
        Assert.NotEqual(copy, copy2);
    }

    [Fact]
    public async Task Assignment_rules()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = test.Service();
        var table = await svc.CreateOkAsync(MonitorTestSupport.OperStatus());
        var scalar = await svc.CreateOkAsync(new MonitorEditModel { Name = "Uptime", OidMode = OidMode.Scalar, Oid = "sysUpTime.0" });
        var node = await test.AddNodeAsync("lab-rtr-hub-01", "192.0.2.11");

        Assert.True((await svc.AddAssignmentAsync(table, AssignmentScope.Node, node, null, "3", "Gi3", Ct)).Ok);
        Assert.False((await svc.AddAssignmentAsync(table, AssignmentScope.Node, node, null, "3", null, Ct)).Ok); // duplicate
        Assert.True((await svc.AddAssignmentAsync(table, AssignmentScope.Node, node, null, null, null, Ct)).Ok); // all rows
        Assert.True((await svc.AddAssignmentAsync(table, AssignmentScope.Tag, null, "core", null, null, Ct)).Ok);
        Assert.False((await svc.AddAssignmentAsync(scalar, AssignmentScope.Node, node, null, "3", null, Ct)).Ok); // scalars have no instance
        Assert.False((await svc.AddAssignmentAsync(table, AssignmentScope.Node, node, null, "3; drop", null, Ct)).Ok);
        Assert.False((await svc.AddAssignmentAsync(table, AssignmentScope.Tag, null, " ", null, null, Ct)).Ok);

        var assignments = (await svc.GetAsync(table, Ct))!.Value.Assignments;
        Assert.Equal(["lab-rtr-hub-01", "lab-rtr-hub-01", "tag core"], assignments.Select(a => a.Target));
        Assert.Contains(assignments, a => a.Rows == "3 (Gi3)");

        // Turning a table monitor with picked rows into a scalar would orphan those rows.
        var model = (await svc.GetAsync(table, Ct))!.Value.Model;
        model.OidMode = OidMode.Scalar;
        model.LabelOid = model.FilterOid = model.FilterValue = null;
        Assert.Equal("OidMode", (await svc.UpdateAsync(table, model, Ct)).Field);
    }

    [Fact]
    public async Task Deleting_a_monitor_removes_and_audits_its_assignments()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = test.Service();
        var id = await svc.CreateOkAsync(MonitorTestSupport.OperStatus());
        await svc.AddAssignmentAsync(id, AssignmentScope.Tag, null, "core", null, null, Ct);

        Assert.True((await svc.DeleteAsync(id, Ct)).Ok);

        await using var db = test.Open();
        Assert.Empty(await db.MonitorAssignments.ToListAsync(Ct));
        Assert.Contains(await db.AuditEntries.ToListAsync(Ct), a => a.EntityType == "MonitorAssignment" && a.Action == "Delete");
    }

    [Fact]
    public async Task Starter_library_is_valid_disabled_and_seeded_once()
    {
        await using var test = await TestDatabase.CreateAsync();
        await StarterLibrary.SeedAsync(test.Factory, test.Clock, Ct);
        var svc = test.Service();
        var list = await svc.ListAsync(Ct);

        Assert.Equal(6, list.Count);
        Assert.All(list, m => Assert.True(m.IsStarter && !m.Enabled && m.AssignmentCount == 0));
        foreach (var m in list)
        {
            var (spec, errors) = svc.BuildSpec((await svc.GetAsync(m.Id, Ct))!.Value.Model);
            Assert.True(errors.Count == 0, $"{m.Name}: {string.Join("; ", errors.Select(e => e.Error))}");
        }

        await svc.DeleteAsync(list[0].Id, Ct);
        await StarterLibrary.SeedAsync(test.Factory, test.Clock, Ct);
        Assert.Equal(5, (await svc.ListAsync(Ct)).Count); // a deleted example stays deleted
    }
}

public class MonitorPlannerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<int> SnmpNodeAsync(TestDatabase test, string hostname, string ip, bool snmp = true)
    {
        var id = await test.AddNodeAsync(hostname, ip, icmp: false);
        var profile = await new CredentialService(test.Factory, test.Protector, test.Clock).CreateAsync(new CredentialEditModel
        {
            Name = "p-" + hostname, Kind = Core.Credentials.CredentialKind.SnmpV3, Username = "yanmt-ro", Port = 161,
            AuthPassword = "lab-auth-pass-01", PrivPassword = "lab-priv-pass-01",
        }, Ct);
        await using var db = test.Open();
        var n = await db.Nodes.SingleAsync(x => x.Id == id, Ct);
        n.SnmpEnabled = snmp;
        n.SnmpProfileId = profile.Id;
        await db.SaveChangesAsync(Ct);
        return id;
    }

    [Fact]
    public async Task Tags_are_live_and_one_monitor_is_polled_once_per_node()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = test.Service();
        var mon = await svc.CreateOkAsync(MonitorTestSupport.OperStatus());
        var a = await SnmpNodeAsync(test, "lab-rtr-a", "192.0.2.1");
        var b = await SnmpNodeAsync(test, "lab-rtr-b", "192.0.2.2");
        var off = await SnmpNodeAsync(test, "lab-rtr-off", "192.0.2.3", snmp: false);
        await svc.AddAssignmentAsync(mon, AssignmentScope.Tag, null, "Core", null, null, Ct);
        await svc.AddAssignmentAsync(mon, AssignmentScope.Node, a, null, "7", "Gi7", Ct);
        await test.TagAsync(a, "core");
        await test.TagAsync(off, "core");

        await using var db = test.Open();
        var plan = await MonitorPlanner.PlanAsync(db, Ct);

        var work = Assert.Single(plan[a]);
        Assert.True(work.AllRows);
        Assert.Equal("Gi7", work.Instances["7"]);
        Assert.False(plan.ContainsKey(b)); // not tagged (yet)
        Assert.False(plan.ContainsKey(off)); // SNMP off: not polled...
        Assert.True((await MonitorPlanner.PlanAsync(db, Ct, activeOnly: false)).ContainsKey(off)); // ...but still applies

        await test.TagAsync(b, "CORE"); // tag names are case-insensitive
        Assert.True((await MonitorPlanner.PlanAsync(db, Ct)).ContainsKey(b));
    }

    [Fact]
    public async Task Disabled_monitors_are_not_polled()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = test.Service();
        var mon = await svc.CreateOkAsync(MonitorTestSupport.OperStatus());
        var a = await SnmpNodeAsync(test, "lab-rtr-a", "192.0.2.1");
        await svc.AddAssignmentAsync(mon, AssignmentScope.Node, a, null, null, null, Ct);
        await svc.SetEnabledAsync(mon, false, Ct);

        await using var db = test.Open();
        Assert.Empty(await MonitorPlanner.PlanAsync(db, Ct));
    }
}

public class MonitorPollingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task All_rows_through_the_admin_up_filter_with_rules_events_and_row_removal()
    {
        await using var test = await TestDatabase.CreateAsync();
        using var agent = new FakeSnmpAgent(Cred, CiscoDevice()); // Gi1 admin up/oper up, Gi2 up/down, Gi3 admin down
        var node = await test.AddSnmpNodeAsync(agent);
        var svc = test.Service();
        var mon = await svc.CreateOkAsync(MonitorTestSupport.OperStatus());
        await svc.AddAssignmentAsync(mon, AssignmentScope.Tag, null, "core", null, null, Ct);
        await test.TagAsync(node, "core");
        var (engine, cache, _) = test.Engine();

        await test.PollAsync(engine, node, agent);

        var rows = cache.Custom(node);
        Assert.Equal(["If oper status · GigabitEthernet1", "If oper status · GigabitEthernet2"], rows.Select(r => r.DisplayName));
        Assert.Equal([Severity.Normal, Severity.Critical], rows.Select(r => r.State));
        Assert.Equal("down (2)", rows[1].ValueText);
        Assert.Equal(Severity.Critical, cache.StateOf(node));

        // Gi2 comes up: an event (it isn't the first data any more).
        agent.Set("1.3.6.1.2.1.2.2.1.8.2", new Integer32(1));
        test.Clock.Now = test.Clock.Now.AddMinutes(5);
        await test.PollAsync(engine, node, agent);
        Assert.All(cache.Custom(node), r => Assert.Equal(Severity.Normal, r.State));
        await using (var db = test.Open())
        {
            var e = await db.Events.SingleAsync(Ct);
            Assert.Equal((StatusSourceType.Custom, Severity.Critical, Severity.Normal), (e.Source, e.OldState!.Value, e.NewState));
            Assert.Contains("If oper status · GigabitEthernet2 Critical → Normal: up (1), rule 1", e.Message);
            Assert.Equal(4, await db.MetricSamples.CountAsync(Ct));
        }

        // Gi1 is shut down: it drops out of the filter, so its instance and state go away.
        agent.Set("1.3.6.1.2.1.2.2.1.7.1", new Integer32(2));
        test.Clock.Now = test.Clock.Now.AddMinutes(5);
        await test.PollAsync(engine, node, agent);
        Assert.Equal(["If oper status · GigabitEthernet2"], cache.Custom(node).Select(r => r.DisplayName));
        await using (var db = test.Open())
        {
            Assert.Equal(1, await db.MonitorInstances.CountAsync(Ct));
        }
    }

    [Fact]
    public async Task Rate_percent_needs_two_polls_then_gives_utilization()
    {
        await using var test = await TestDatabase.CreateAsync();
        using var agent = new FakeSnmpAgent(Cred, CiscoDevice());
        var node = await test.AddSnmpNodeAsync(agent);
        var svc = test.Service();
        var mon = await svc.CreateOkAsync(new MonitorEditModel
        {
            Name = "Util in", OidMode = OidMode.Table, Oid = "ifHCInOctets", LabelOid = "ifDescr", ValueMode = ValueMode.Rate,
            Scale = "8", DenominatorOid = "ifHighSpeed", DenominatorScale = "1000000", Unit = "%",
            Rules = [new RuleRow { Operator = RuleOperator.GreaterOrEqual, Value = "50", State = Severity.Warning }],
        });
        await svc.AddAssignmentAsync(mon, AssignmentScope.Node, node, null, "1", "GigabitEthernet1", Ct);
        var (engine, cache, _) = test.Engine();

        await test.PollAsync(engine, node, agent);
        var first = Assert.Single(cache.Custom(node));
        Assert.Equal((Severity.Unknown, "waiting for a second sample"), (first.State, first.ValueText));

        // 625 MB in 10 s on a 1000 Mb/s port = 500 Mb/s = 50%.
        agent.Set($"{IfHcInOctets}.1", new Counter64(1_000_000 + 625_000_000));
        test.Clock.Now = test.Clock.Now.AddSeconds(10);
        await test.PollAsync(engine, node, agent);

        var util = Assert.Single(cache.Custom(node));
        Assert.Equal((Severity.Warning, "50%", 50.0), (util.State, util.ValueText, util.Value!.Value));
        await using var db = test.Open();
        Assert.Empty(await db.Events.ToListAsync(Ct)); // waiting -> first value isn't an event
    }

    [Fact]
    public async Task A_picked_row_whose_label_changed_goes_warning()
    {
        await using var test = await TestDatabase.CreateAsync();
        using var agent = new FakeSnmpAgent(Cred, CiscoDevice());
        var node = await test.AddSnmpNodeAsync(agent);
        var svc = test.Service();
        var mon = await svc.CreateOkAsync(MonitorTestSupport.OperStatus(filterAdminUp: false));
        await svc.AddAssignmentAsync(mon, AssignmentScope.Node, node, null, "1", "Tunnel0", Ct); // picked as Tunnel0
        var (engine, cache, _) = test.Engine();

        await test.PollAsync(engine, node, agent);

        var row = Assert.Single(cache.Custom(node));
        Assert.Equal(Severity.Warning, row.State);
        Assert.Equal("instance changed: was Tunnel0, now GigabitEthernet1", row.ValueText);
    }

    [Fact]
    public async Task Consecutive_matches_dampen_a_flap()
    {
        await using var test = await TestDatabase.CreateAsync();
        using var agent = new FakeSnmpAgent(Cred, CiscoDevice());
        var node = await test.AddSnmpNodeAsync(agent);
        var svc = test.Service();
        var model = MonitorTestSupport.OperStatus(filterAdminUp: false);
        model.ConsecutiveMatches = 2;
        var mon = await svc.CreateOkAsync(model);
        await svc.AddAssignmentAsync(mon, AssignmentScope.Node, node, null, "1", null, Ct);
        var (engine, cache, _) = test.Engine();
        await test.PollAsync(engine, node, agent);

        agent.Set("1.3.6.1.2.1.2.2.1.8.1", new Integer32(2)); // down once...
        await test.PollAsync(engine, node, agent);
        Assert.Equal(Severity.Normal, Assert.Single(cache.Custom(node)).State);
        agent.Set("1.3.6.1.2.1.2.2.1.8.1", new Integer32(1)); // ...and back: never went red
        await test.PollAsync(engine, node, agent);
        agent.Set("1.3.6.1.2.1.2.2.1.8.1", new Integer32(2)); // down twice in a row: Critical
        await test.PollAsync(engine, node, agent);
        await test.PollAsync(engine, node, agent);
        Assert.Equal(Severity.Critical, Assert.Single(cache.Custom(node)).State);
    }

    [Fact]
    public async Task Failed_poll_is_quiet_unknown_and_untagging_deletes_while_disabling_keeps_history()
    {
        await using var test = await TestDatabase.CreateAsync();
        using var agent = new FakeSnmpAgent(Cred, CiscoDevice());
        var node = await test.AddSnmpNodeAsync(agent);
        var svc = test.Service();
        var mon = await svc.CreateOkAsync(MonitorTestSupport.OperStatus());
        await svc.AddAssignmentAsync(mon, AssignmentScope.Tag, null, "core", null, null, Ct);
        await test.TagAsync(node, "core");
        var (engine, cache, _) = test.Engine();
        await test.PollAsync(engine, node, agent);

        // A failed poll: every instance Unknown, no events.
        await using (var db = test.Open())
        {
            var works = (await MonitorPlanner.PlanAsync(db, Ct))[node];
            await engine.ApplyAsync([new MonitorPollResult(node, test.Clock.GetUtcNow().UtcDateTime, works, null, Core.Connectivity.FailureCategory.Timeout, "no reply")], Ct);
        }

        Assert.All(cache.Custom(node), r => Assert.Equal((Severity.Unknown, "SNMP timeout"), (r.State, r.ValueText)));
        await using (var db = test.Open())
        {
            Assert.Empty(await db.Events.ToListAsync(Ct));
        }

        // Disabling the monitor switches its states off but keeps instances and samples.
        await svc.SetEnabledAsync(mon, false, Ct);
        await engine.SyncAsync(Ct);
        Assert.Empty(cache.Custom(node));
        await using (var db = test.Open())
        {
            Assert.Equal(2, await db.MonitorInstances.CountAsync(Ct));
            Assert.Equal(2, await db.MetricSamples.CountAsync(Ct));
        }

        // Removing the node from the tag removes them for good.
        await using (var db = test.Open())
        {
            db.NodeTags.RemoveRange(await db.NodeTags.ToListAsync(Ct));
            await db.SaveChangesAsync(Ct);
        }

        await engine.SyncAsync(Ct);
        await using (var db = test.Open())
        {
            Assert.Empty(await db.MonitorInstances.ToListAsync(Ct));
            Assert.Empty(await db.StatusSources.Where(s => s.SourceType == StatusSourceType.Custom).ToListAsync(Ct));
            Assert.Empty(await db.MetricSamples.ToListAsync(Ct));
        }
    }

    [Fact]
    public async Task Scalars_and_tables_on_one_node_share_the_requests()
    {
        await using var test = await TestDatabase.CreateAsync();
        using var agent = new FakeSnmpAgent(Cred, CiscoDevice());
        var node = await test.AddSnmpNodeAsync(agent);
        var svc = test.Service();
        var scalar = await svc.CreateOkAsync(new MonitorEditModel
        {
            Name = "Uptime days", OidMode = OidMode.Scalar, Oid = "sysUpTime.0", Scale = "0.000000115740740740741", Unit = "days",
            Rules = [new RuleRow { Operator = RuleOperator.Less, Value = "1", State = Severity.Warning }],
        });
        var table = await svc.CreateOkAsync(MonitorTestSupport.OperStatus());
        await svc.AddAssignmentAsync(scalar, AssignmentScope.Node, node, null, null, null, Ct);
        await svc.AddAssignmentAsync(table, AssignmentScope.Node, node, null, null, null, Ct);
        var (engine, cache, _) = test.Engine();

        await test.PollAsync(engine, node, agent);

        Assert.Equal(3, cache.Custom(node).Count);
        Assert.Equal("1 days", cache.Custom(node).Single(s => s.DisplayName == "Uptime days").ValueText);
        Assert.Equal((1, 1), (agent.GetRequestsAnswered, agent.BulkRequestsAnswered)); // one GET, one walk
    }
}

public class MonitorProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MonitorProbeService Probe(TestDatabase test, MonitorService monitors)
    {
        var reader = new SnmpV3Reader();
        var resolver = new SnmpTargetResolver(test.Factory, new CredentialService(test.Factory, test.Protector, test.Clock), new SettingsService(test.Factory, test.Clock));
        return new MonitorProbeService(resolver, new MonitorCollector(reader), reader, monitors, TimeProvider.System);
    }

    [Fact]
    public async Task Test_on_node_evaluates_every_row_and_marks_filtered_ones()
    {
        await using var test = await TestDatabase.CreateAsync();
        using var agent = new FakeSnmpAgent(Cred, CiscoDevice());
        var node = await test.AddSnmpNodeAsync(agent);
        var svc = test.Service();
        var (spec, errors) = svc.BuildSpec(MonitorTestSupport.OperStatus());
        Assert.Empty(errors);

        var r = await Probe(test, svc).TestAsync(spec, node, null, TimeSpan.Zero, Ct);

        Assert.Null(r.Error);
        Assert.Equal(["1", "2", "3"], r.Rows.Select(x => x.Key));
        Assert.Equal([Severity.Normal, Severity.Critical, Severity.Critical], r.Rows.Select(x => x.State));
        Assert.Equal([false, false, true], r.Rows.Select(x => x.FilteredOut));
        Assert.Equal("rule 1", r.Rows[0].Explanation);

        var one = await Probe(test, svc).TestAsync(spec, node, "2", TimeSpan.Zero, Ct);
        Assert.Equal("down (2)", Assert.Single(one.Rows).Value);
    }

    [Fact]
    public async Task Picker_lists_rows_and_monitor_this_stores_the_label()
    {
        await using var test = await TestDatabase.CreateAsync();
        using var agent = new FakeSnmpAgent(Cred, CiscoDevice());
        var node = await test.AddSnmpNodeAsync(agent);
        var svc = test.Service();
        var mon = await svc.CreateOkAsync(MonitorTestSupport.OperStatus());
        var probe = Probe(test, svc);

        var (choices, error) = await probe.ListInstancesAsync((await svc.GetSpecAsync(mon, Ct))!, node, Ct);
        Assert.Null(error);
        Assert.Equal([("1", "GigabitEthernet1"), ("2", "GigabitEthernet2"), ("3", "GigabitEthernet3")], choices.Select(c => (c.Suffix, c.Label)));

        Assert.True((await probe.AddInstanceAsync(mon, node, "2", Ct)).Ok);
        Assert.Equal("2 (GigabitEthernet2)", Assert.Single((await svc.GetAsync(mon, Ct))!.Value.Assignments).Rows);
        Assert.Equal([(mon, "If oper status")], await svc.InterfaceMonitorsAsync(Ct));
    }
}

public class LinkBindingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Links_bind_to_monitor_instances_on_their_end_nodes_only()
    {
        await using var test = await TestDatabase.CreateAsync();
        using var agent = new FakeSnmpAgent(Cred, CiscoDevice());
        var hub = await test.AddSnmpNodeAsync(agent);
        var spoke = await test.AddNodeAsync("lab-rtr-spoke-01", "192.0.2.21");
        var other = await test.AddNodeAsync("lab-rtr-other-01", "192.0.2.31");
        var svc = test.Service();
        var mon = await svc.CreateOkAsync(MonitorTestSupport.OperStatus(filterAdminUp: false));
        await svc.AddAssignmentAsync(mon, AssignmentScope.Node, hub, null, "1", null, Ct);
        var (engine, cache, _) = test.Engine();
        await test.PollAsync(engine, hub, agent);

        var maps = new MapService(test.Factory, test.Paths, test.Clock);
        var home = await maps.GetHomeMapIdAsync(Ct);
        var a = (await maps.AddNodeAsync(home, hub, 10, 10, Ct)).Id!.Value;
        var b = (await maps.AddNodeAsync(home, spoke, 90, 90, Ct)).Id!.Value;
        var link = (await maps.AddLinkAsync(home, a, b, Ct)).Id!.Value;

        var choice = Assert.Single(await maps.LinkBindingChoicesAsync(link, Ct));
        Assert.Equal(("lab-rtr-hub-01", "If oper status · GigabitEthernet1"), (choice.Hostname, choice.Name));
        Assert.True((await maps.SetLinkBindingAsync(link, choice.InstanceId, true, Ct)).Ok);

        var view = (await maps.GetAsync(home, Ct))!.Links.Single();
        Assert.Equal((choice.InstanceId, hub, true), (view.BoundInstanceId!.Value, view.BoundNodeId!.Value, view.ShowBoundValue));
        Assert.Equal(Severity.Normal, cache.Get(hub)!.Source(StatusSourceType.Custom, choice.InstanceId.ToString())!.State);

        // An instance on a node the link doesn't touch is refused.
        await using (var db = test.Open())
        {
            db.MonitorInstances.Add(new MonitorInstance { DefinitionId = mon, NodeId = other, InstanceKey = "9" });
            await db.SaveChangesAsync(Ct);
        }

        var foreign = (await test.Open().MonitorInstances.SingleAsync(i => i.NodeId == other, Ct)).Id;
        Assert.False((await maps.SetLinkBindingAsync(link, foreign, false, Ct)).Ok);

        Assert.True((await maps.SetLinkBindingAsync(link, null, true, Ct)).Ok);
        Assert.Equal((null, false), ((await maps.GetAsync(home, Ct))!.Links.Single().BoundInstanceId, (await maps.GetAsync(home, Ct))!.Links.Single().ShowBoundValue));
    }
}
