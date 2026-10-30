using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Data.Entities;
using Yanmt.Infrastructure.Events;
using Yanmt.Infrastructure.Security;
using static Yanmt.Infrastructure.Tests.Monitoring.MonitoringTestSupport;

namespace Yanmt.Infrastructure.Tests.Events;

public class EventServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ClaimsPrincipal Admin = new(new ClaimsIdentity([new Claim(ClaimTypes.Role, YanmtRoles.Admin)], "test", ClaimTypes.Name, ClaimTypes.Role));
    private static readonly ClaimsPrincipal Viewer = new(new ClaimsIdentity([new Claim(ClaimTypes.Role, YanmtRoles.Viewer)], "test", ClaimTypes.Name, ClaimTypes.Role));

    private static EventService Service(TestDatabase test) =>
        new(test.Factory, new SystemUser(), new AuditLogService(test.Factory, new SystemUser(), test.Clock), test.Clock, new InAppNotifier());

    private static async Task AddEventsAsync(TestDatabase test, int nodeA, int nodeB)
    {
        await using var db = test.Open();
        var t = test.Clock.GetUtcNow().UtcDateTime;
        db.Events.AddRange(
            new Event { TimestampUtc = t, NodeId = nodeA, Source = StatusSourceType.Icmp, OldState = Severity.Normal, NewState = Severity.Critical, Message = "a icmp critical" },
            new Event { TimestampUtc = t.AddMinutes(1), NodeId = nodeA, Source = StatusSourceType.Cpu, OldState = Severity.Normal, NewState = Severity.Warning, Message = "a cpu warning" },
            new Event { TimestampUtc = t.AddMinutes(2), NodeId = nodeB, Source = StatusSourceType.Icmp, OldState = Severity.Normal, NewState = Severity.Critical, Message = "b icmp critical" });
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Query_filters_by_node_state_source_and_orders_newest_first()
    {
        await using var test = await TestDatabase.CreateAsync();
        var a = await test.AddNodeAsync("lab-rtr-a", "192.0.2.1");
        var b = await test.AddNodeAsync("lab-rtr-b", "192.0.2.2");
        await AddEventsAsync(test, a, b);
        var events = Service(test);

        var all = await events.QueryAsync(new EventFilter(), 0, 10, Ct);
        Assert.Equal(["b icmp critical", "a cpu warning", "a icmp critical"], all.Rows.Select(r => r.Message));
        Assert.Equal("lab-rtr-b", all.Rows[0].Hostname);
        Assert.Equal(2, (await events.QueryAsync(new EventFilter(NodeId: a), 0, 10, Ct)).Total);
        Assert.Equal(2, (await events.QueryAsync(new EventFilter(State: Severity.Critical), 0, 10, Ct)).Total);
        Assert.Equal(1, (await events.QueryAsync(new EventFilter(a, Severity.Critical, StatusSourceType.Icmp), 0, 10, Ct)).Total);

        var page = await events.QueryAsync(new EventFilter(), 1, 1, Ct);
        Assert.Equal(3, page.Total);
        Assert.Equal("a cpu warning", Assert.Single(page.Rows).Message);
    }

    [Fact]
    public async Task Critical_count_covers_only_unacknowledged_critical_and_follows_acknowledgement()
    {
        await using var test = await TestDatabase.CreateAsync();
        var a = await test.AddNodeAsync("lab-rtr-a", "192.0.2.1");
        await AddEventsAsync(test, a, a);
        var notifier = new InAppNotifier();
        var acks = 0;
        notifier.Acknowledged += () => acks++;
        var events = new EventService(test.Factory, new SystemUser(), new AuditLogService(test.Factory, new SystemUser(), test.Clock), test.Clock, notifier);

        Assert.Equal(2, await events.CountUnacknowledgedCriticalAsync(Ct));
        var id = (await events.QueryAsync(new EventFilter(State: Severity.Critical), 0, 1, Ct)).Rows[0].Id;
        await events.AcknowledgeAsync(id, Admin, Ct);
        Assert.Equal(1, await events.CountUnacknowledgedCriticalAsync(Ct));
        await events.AcknowledgeAllAsync(new EventFilter(), Admin, Ct);
        Assert.Equal(0, await events.CountUnacknowledgedCriticalAsync(Ct));
        Assert.Equal(2, acks);
    }

    [Fact]
    public async Task Export_csv_honors_filter_and_has_no_extra_columns()
    {
        await using var test = await TestDatabase.CreateAsync();
        var a = await test.AddNodeAsync("lab-rtr-a", "192.0.2.1");
        var b = await test.AddNodeAsync("lab-rtr-b", "192.0.2.2");
        await AddEventsAsync(test, a, b);
        var events = Service(test);

        var all = Yanmt.Core.Csv.CsvCodec.Parse(await events.ExportCsvAsync(new EventFilter(), Ct));
        Assert.Equal(Yanmt.Core.Status.EventCsv.Columns, all[0]);
        Assert.Equal(["b icmp critical", "a cpu warning", "a icmp critical"], all.Skip(1).Select(r => r[6]));
        Assert.Equal("lab-rtr-b", all[1][2]);

        var crit = Yanmt.Core.Csv.CsvCodec.Parse(await events.ExportCsvAsync(new EventFilter(NodeId: a, State: Severity.Critical), Ct));
        Assert.Equal(["a icmp critical"], crit.Skip(1).Select(r => r[6]));
    }

    [Fact]
    public async Task Acknowledge_marks_one_event_once_and_audits_it()
    {
        await using var test = await TestDatabase.CreateAsync();
        var a = await test.AddNodeAsync("lab-rtr-a", "192.0.2.1");
        await AddEventsAsync(test, a, a);
        var events = Service(test);
        var id = (await events.QueryAsync(new EventFilter(), 0, 1, Ct)).Rows[0].Id;

        Assert.True(await events.AcknowledgeAsync(id, Admin, Ct));
        Assert.False(await events.AcknowledgeAsync(id, Admin, Ct));

        var row = (await events.QueryAsync(new EventFilter(), 0, 10, Ct)).Rows.Single(r => r.Id == id);
        Assert.Equal((SystemUser.Name, true), (row.AcknowledgedBy, row.AcknowledgedUtc is not null));
        await using var db = test.Open();
        var audit = await db.AuditEntries.SingleAsync(x => x.Action == "AcknowledgeEvent", Ct);
        Assert.Equal(id.ToString(), audit.EntityKey);
    }

    [Fact]
    public async Task Acknowledge_one_is_refused_for_viewers_and_changes_nothing()
    {
        await using var test = await TestDatabase.CreateAsync();
        var a = await test.AddNodeAsync("lab-rtr-a", "192.0.2.1");
        await AddEventsAsync(test, a, a);
        var events = Service(test);
        var id = (await events.QueryAsync(new EventFilter(), 0, 1, Ct)).Rows[0].Id;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => events.AcknowledgeAsync(id, Viewer, Ct));

        Assert.Equal(3, (await events.QueryAsync(new EventFilter(UnacknowledgedOnly: true), 0, 10, Ct)).Total);
        await using var db = test.Open();
        Assert.Empty(await db.AuditEntries.Where(x => x.Action == "AcknowledgeEvent").ToListAsync(Ct));
    }

    [Fact]
    public async Task Count_unacknowledged_ignores_already_acknowledged_events()
    {
        await using var test = await TestDatabase.CreateAsync();
        var a = await test.AddNodeAsync("lab-rtr-a", "192.0.2.1");
        await AddEventsAsync(test, a, a);
        var events = Service(test);
        var id = (await events.QueryAsync(new EventFilter(), 0, 1, Ct)).Rows[0].Id;
        await events.AcknowledgeAsync(id, Admin, Ct);

        Assert.Equal(2, await events.CountUnacknowledgedAsync(new EventFilter(), Ct));
    }

    [Fact]
    public async Task Acknowledge_all_touches_only_the_current_filter_and_is_audited()
    {
        await using var test = await TestDatabase.CreateAsync();
        var a = await test.AddNodeAsync("lab-rtr-a", "192.0.2.1");
        var b = await test.AddNodeAsync("lab-rtr-b", "192.0.2.2");
        await AddEventsAsync(test, a, b);
        var events = Service(test);

        var count = await events.AcknowledgeAllAsync(new EventFilter(NodeId: a), Admin, Ct);

        Assert.Equal(2, count);
        var left = await events.QueryAsync(new EventFilter(UnacknowledgedOnly: true), 0, 10, Ct);
        Assert.Equal("b icmp critical", Assert.Single(left.Rows).Message);
        await using var db = test.Open();
        var audit = await db.AuditEntries.SingleAsync(x => x.Action == "AcknowledgeAllEvents", Ct);
        Assert.Contains("\"count\":2", audit.Changes);
    }

    [Fact]
    public async Task Acknowledge_all_is_refused_for_viewers_and_changes_nothing()
    {
        await using var test = await TestDatabase.CreateAsync();
        var a = await test.AddNodeAsync("lab-rtr-a", "192.0.2.1");
        await AddEventsAsync(test, a, a);
        var events = Service(test);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => events.AcknowledgeAllAsync(new EventFilter(), Viewer, Ct));

        Assert.Equal(3, (await events.QueryAsync(new EventFilter(UnacknowledgedOnly: true), 0, 10, Ct)).Total);
        await using var db = test.Open();
        Assert.Empty(await db.AuditEntries.Where(x => x.Action == "AcknowledgeAllEvents").ToListAsync(Ct));
    }
}
