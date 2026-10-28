using Microsoft.EntityFrameworkCore;
using Yanmt.Core.Monitoring;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Data.Entities;
using Yanmt.Infrastructure.Events;
using Yanmt.Infrastructure.Monitoring;
using static Yanmt.Infrastructure.Tests.Monitoring.MonitoringTestSupport;

namespace Yanmt.Infrastructure.Tests.Monitoring;

public class StatusEngineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IcmpSettings Settings = new();

    private static IcmpPollResult Poll(TestDatabase test, int nodeId, Infrastructure.Collectors.Icmp.IcmpResult? result, string? error = null)
    {
        test.Clock.Now = test.Clock.Now.AddMinutes(1);
        return new IcmpPollResult(nodeId, test.Clock.GetUtcNow().UtcDateTime, Settings, result, error);
    }

    private static async Task<StatusSource> IcmpSourceAsync(TestDatabase test, int nodeId)
    {
        await using var db = test.Open();
        return await db.StatusSources.SingleAsync(s => s.NodeId == nodeId && s.SourceType == StatusSourceType.Icmp, Ct);
    }

    [Fact]
    public async Task First_reply_creates_the_source_sample_and_cache_entry_without_an_event()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-hub-01", "192.0.2.11");
        var (engine, cache, broadcaster) = test.Engine();
        var published = new List<int>();
        broadcaster.Changed += ids => published.AddRange(ids);

        await engine.ApplyAsync([Poll(test, node, Replies(3))], Ct);

        var source = await IcmpSourceAsync(test, node);
        Assert.Equal(Severity.Normal, source.State);
        Assert.Equal("3 ms", source.ValueText);
        await using var db = test.Open();
        var sample = await db.IcmpSamples.SingleAsync(Ct);
        Assert.Equal((node, 4, 4, 3.0, 0.0), (sample.NodeId, sample.Sent, sample.Received, sample.AvgMs!.Value, sample.LossPercent));
        Assert.Empty(await db.Events.ToListAsync(Ct)); // Unknown -> first data isn't a change

        Assert.Equal(Severity.Normal, cache.StateOf(node));
        Assert.Equal("3 ms", cache.Get(node)!.Source(StatusSourceType.Icmp)!.ValueText);
        broadcaster.Flush();
        Assert.Equal([node], published);
    }

    [Fact]
    public async Task Outage_goes_warning_then_critical_with_events_and_recovers()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-spoke-01", "192.0.2.21");
        var (engine, cache, _) = test.Engine();

        await engine.ApplyAsync([Poll(test, node, Replies(2))], Ct);
        await engine.ApplyAsync([Poll(test, node, NoReply)], Ct);
        Assert.Equal(Severity.Warning, cache.StateOf(node));
        await engine.ApplyAsync([Poll(test, node, NoReply)], Ct);
        Assert.Equal(Severity.Warning, cache.StateOf(node));
        await engine.ApplyAsync([Poll(test, node, NoReply)], Ct);
        Assert.Equal(Severity.Critical, cache.StateOf(node));
        await engine.ApplyAsync([Poll(test, node, Replies(2))], Ct);
        Assert.Equal(Severity.Normal, cache.StateOf(node));

        await using var db = test.Open();
        var events = await db.Events.OrderBy(e => e.Id).ToListAsync(Ct);
        Assert.Equal(
            [(Severity.Normal, Severity.Warning), (Severity.Warning, Severity.Critical), (Severity.Critical, Severity.Normal)],
            events.Select(e => (e.OldState!.Value, e.NewState)));
        Assert.All(events, e => Assert.StartsWith("lab-rtr-spoke-01: ICMP ", e.Message));
        Assert.Contains("3 consecutive", events[1].Message);
        Assert.Equal(5, await db.IcmpSamples.CountAsync(Ct));
    }

    [Fact]
    public async Task Dampening_counter_survives_between_batches_in_the_db()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-hub-01", "192.0.2.11");
        var (engine, _, _) = test.Engine();

        await engine.ApplyAsync([Poll(test, node, Replies(2))], Ct);
        await engine.ApplyAsync([Poll(test, node, NoReply)], Ct);

        var source = await IcmpSourceAsync(test, node);
        Assert.Equal((Severity.Warning, Severity.Critical, 1), (source.State, source.PendingState, source.PendingCount));

        // A fresh engine (e.g. after a restart) continues the count from the DB.
        var (restarted, cache, _) = test.Engine();
        await restarted.ApplyAsync([Poll(test, node, NoReply), Poll(test, node, NoReply)], Ct);
        Assert.Equal(Severity.Critical, cache.StateOf(node));
    }

    [Fact]
    public async Task A_failed_poll_is_unknown_not_critical()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-hub-01", "192.0.2.11");
        var (engine, cache, _) = test.Engine();

        await engine.ApplyAsync([Poll(test, node, Replies(2))], Ct);
        await engine.ApplyAsync([Poll(test, node, null, "Ping threw")], Ct);

        var source = await IcmpSourceAsync(test, node);
        Assert.Equal((Severity.Unknown, "poll failed"), (source.State, source.ValueText));
        Assert.Equal(Severity.Unknown, cache.StateOf(node));
        await using var db = test.Open();
        Assert.Contains("Ping threw", (await db.Events.SingleAsync(Ct)).Message);
        Assert.Equal(1, await db.IcmpSamples.CountAsync(Ct)); // no sample for a failed poll
    }

    [Fact]
    public async Task Results_for_disabled_or_deleted_nodes_are_ignored()
    {
        await using var test = await TestDatabase.CreateAsync();
        var disabled = await test.AddNodeAsync("lab-rtr-off-01", "192.0.2.31", enabled: false);
        var noIcmp = await test.AddNodeAsync("lab-sw-noping-01", "192.0.2.32", icmp: false);
        var (engine, _, _) = test.Engine();

        await engine.ApplyAsync([Poll(test, disabled, Replies(2)), Poll(test, noIcmp, Replies(2)), Poll(test, 9999, Replies(2))], Ct);

        await using var db = test.Open();
        Assert.Empty(await db.StatusSources.ToListAsync(Ct));
        Assert.Empty(await db.IcmpSamples.ToListAsync(Ct));
    }

    [Fact]
    public async Task Sync_disables_icmp_when_turned_off_and_reenables_it_as_unknown()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-hub-01", "192.0.2.11");
        var (engine, cache, _) = test.Engine();
        await engine.ApplyAsync([Poll(test, node, Replies(2))], Ct);

        await SetIcmpAsync(test, node, false);
        await engine.SyncAsync(Ct);
        var off = await IcmpSourceAsync(test, node);
        Assert.False(off.Enabled);
        Assert.Equal(Severity.Unknown, off.State);
        Assert.Empty(cache.Get(node)!.Sources); // out of the rollup
        Assert.Equal(Severity.Unknown, cache.StateOf(node));

        await SetIcmpAsync(test, node, true);
        await engine.SyncAsync(Ct);
        var on = await IcmpSourceAsync(test, node);
        Assert.True(on.Enabled);
        Assert.Equal(Severity.Unknown, on.State);

        // The first poll after re-enabling is not recorded as a state change.
        await engine.ApplyAsync([Poll(test, node, Replies(2))], Ct);
        await using var db = test.Open();
        Assert.Empty(await db.Events.ToListAsync(Ct));
    }

    [Fact]
    public async Task Disabled_node_rolls_up_unknown_and_deleted_node_leaves_the_cache()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-hub-01", "192.0.2.11");
        var (engine, cache, _) = test.Engine();
        await engine.ApplyAsync([Poll(test, node, Replies(2))], Ct);

        await using (var db = test.Open())
        {
            var n = await db.Nodes.SingleAsync(Ct);
            n.Enabled = false;
            await db.SaveChangesAsync(Ct);
        }

        await engine.SyncAsync(Ct);
        Assert.Equal((false, Severity.Unknown), (cache.Get(node)!.Enabled, cache.StateOf(node)));

        await using (var db = test.Open())
        {
            db.Nodes.Remove(await db.Nodes.SingleAsync(Ct));
            await db.SaveChangesAsync(Ct);
        }

        await engine.SyncAsync(Ct);
        Assert.Null(cache.Get(node));
    }

    [Fact]
    public async Task RunAsync_drains_submitted_results()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-hub-01", "192.0.2.11");
        var (engine, cache, _) = test.Engine();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);

        await engine.SubmitAsync(Poll(test, node, Replies(5)), Ct);
        await WaitUntilAsync(() => cache.StateOf(node) == Severity.Normal);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    private sealed class RecordingNotifier(bool fail = false) : INotifier
    {
        public List<EventNotice> Notices { get; } = [];

        public Task NotifyAsync(IReadOnlyList<EventNotice> events, CancellationToken cancellationToken = default)
        {
            Notices.AddRange(events);
            return fail ? throw new InvalidOperationException("notifier down") : Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Committed_events_are_routed_through_the_notifier_with_their_ids()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-spoke-01", "192.0.2.21");
        var notifier = new RecordingNotifier();
        var (engine, _, _) = test.Engine(notifier);

        await engine.ApplyAsync([Poll(test, node, Replies(2))], Ct);
        Assert.Empty(notifier.Notices); // first data is not an event
        await engine.ApplyAsync([Poll(test, node, NoReply)], Ct);

        var notice = Assert.Single(notifier.Notices);
        await using var db = test.Open();
        var stored = await db.Events.SingleAsync(Ct);
        Assert.Equal((stored.Id, node, Severity.Warning), (notice.Id, notice.NodeId, notice.NewState));
    }

    [Fact]
    public async Task A_failing_notifier_does_not_lose_the_event_or_the_state_update()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-spoke-01", "192.0.2.21");
        var (engine, cache, _) = test.Engine(new RecordingNotifier(fail: true));

        await engine.ApplyAsync([Poll(test, node, Replies(2))], Ct);
        await engine.ApplyAsync([Poll(test, node, NoReply)], Ct);

        Assert.Equal(Severity.Warning, cache.StateOf(node));
        await using var db = test.Open();
        Assert.Single(await db.Events.ToListAsync(Ct));
    }

    internal static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "Timed out waiting for the condition.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private static async Task SetIcmpAsync(TestDatabase test, int nodeId, bool on)
    {
        await using var db = test.Open();
        var n = await db.Nodes.SingleAsync(x => x.Id == nodeId, Ct);
        n.IcmpEnabled = on;
        await db.SaveChangesAsync(Ct);
    }
}

public class StatusBroadcasterTests
{
    [Fact]
    public void Changes_are_coalesced_into_one_notification()
    {
        using var broadcaster = new StatusBroadcaster(TimeProvider.System);
        var batches = new List<IReadOnlyCollection<int>>();
        broadcaster.Changed += batches.Add;

        broadcaster.Publish([1, 2]);
        broadcaster.Publish([2, 3]);
        broadcaster.Flush();
        broadcaster.Flush(); // nothing pending: no empty notification

        Assert.Equal([1, 2, 3], Assert.Single(batches).Order());
    }

    [Fact]
    public void A_throwing_subscriber_does_not_stop_the_others()
    {
        using var broadcaster = new StatusBroadcaster(TimeProvider.System);
        var seen = false;
        broadcaster.Changed += _ => throw new InvalidOperationException("circuit gone");
        broadcaster.Changed += _ => seen = true;

        broadcaster.Publish([1]);
        broadcaster.Flush();

        Assert.True(seen);
    }
}

public class PollScheduleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void New_targets_are_staggered_over_the_first_seconds()
    {
        var schedule = new PollSchedule();
        schedule.SetTargets([1, 2, 3], TimeSpan.FromSeconds(60), T0);

        Assert.Equal([1], schedule.TakeDue(T0));
        Assert.Equal([2], schedule.TakeDue(T0.AddSeconds(5)));
        Assert.Equal([3], schedule.TakeDue(T0.AddSeconds(10)));
        Assert.Empty(schedule.TakeDue(T0.AddSeconds(14)));
    }

    [Fact]
    public void Each_target_keeps_its_cadence()
    {
        var schedule = new PollSchedule();
        schedule.SetTargets([1], TimeSpan.FromSeconds(60), T0);

        Assert.Equal([1], schedule.TakeDue(T0.AddSeconds(0.4)));
        Assert.Empty(schedule.TakeDue(T0.AddSeconds(59)));
        Assert.Equal([1], schedule.TakeDue(T0.AddSeconds(60.3))); // due at T0+60, not "60 s after it was taken"
    }

    [Fact]
    public void A_target_that_fell_far_behind_does_not_burst()
    {
        var schedule = new PollSchedule();
        schedule.SetTargets([1], TimeSpan.FromSeconds(60), T0);
        schedule.TakeDue(T0);

        Assert.Equal([1], schedule.TakeDue(T0.AddMinutes(10)));
        Assert.Empty(schedule.TakeDue(T0.AddMinutes(10).AddSeconds(1)));
    }

    [Fact]
    public void Removed_targets_stop_and_a_shorter_interval_applies_now()
    {
        var schedule = new PollSchedule();
        schedule.SetTargets([1, 2], TimeSpan.FromSeconds(300), T0);
        schedule.TakeDue(T0.AddSeconds(20));

        schedule.SetTargets([2], TimeSpan.FromSeconds(30), T0.AddSeconds(20));

        Assert.Equal(1, schedule.Count);
        Assert.Equal([2], schedule.TakeDue(T0.AddSeconds(50)));
    }
}
