using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Monitoring;
using Yanmt.Infrastructure.Monitors;
using Yanmt.Infrastructure.Security;
using Yanmt.Infrastructure.Settings;
using static Yanmt.Infrastructure.Tests.Monitoring.MonitoringTestSupport;

namespace Yanmt.Infrastructure.Tests.Monitoring;

public class ManualPollTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ClaimsPrincipal Admin = new(new ClaimsIdentity([new Claim(ClaimTypes.Role, YanmtRoles.Admin)], "test", ClaimTypes.Name, ClaimTypes.Role));
    private static readonly ClaimsPrincipal Viewer = new(new ClaimsIdentity([new Claim(ClaimTypes.Role, YanmtRoles.Viewer)], "test", ClaimTypes.Name, ClaimTypes.Role));

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        private readonly Task _engineLoop;

        public Harness(TestDatabase test)
        {
            Test = test;
            var services = new ServiceCollection();
            services.AddSingleton(test.Factory);
            services.AddSingleton<TimeProvider>(test.Clock);
            services.AddSingleton(Signal);
            services.AddScoped<SettingsService>();
            _services = services.BuildServiceProvider();

            var scopes = _services.GetRequiredService<IServiceScopeFactory>();
            (var engine, Cache, _) = test.Engine();
            var options = Options.Create(new PollingOptions());
            Icmp = new IcmpPoller(scopes, engine, Probe, Signal, options, test.Clock, NullLogger<IcmpPoller>.Instance);
            // SNMP and monitor pollers never load here (no SNMP nodes), so their collectors are never used.
            var snmp = new SnmpPoller(scopes, engine, new DefaultStatsCollector(null!), Signal, options, test.Clock, NullLogger<SnmpPoller>.Instance);
            var monitors = new MonitorPoller(scopes, engine, new MonitorCollector(null!), Signal, options, test.Clock, NullLogger<MonitorPoller>.Instance);
            Service = new ManualPollService(Icmp, snmp, monitors, engine, test.Clock);
            _engineLoop = engine.RunAsync(_stop.Token);
        }

        public TestDatabase Test { get; }
        public InventoryChangeSignal Signal { get; } = new();
        public FakeIcmpProbe Probe { get; } = new();
        public StatusCache Cache { get; }
        public IcmpPoller Icmp { get; }
        public ManualPollService Service { get; }

        /// <summary>Loads the pollers' targets (and polls anything due at the clock's current time).</summary>
        public Task LoadAsync() => Icmp.TickAsync(_stop.Token);

        public void Advance(TimeSpan by) => Test.Clock.Now += by;

        public async Task<ManualPollOutcome> PollAsync(int node)
        {
            var outcome = await Service.PollNowAsync(node, Admin, Ct);
            Advance(ManualPollService.MinInterval); // clear the rate limit for the next poll
            return outcome;
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try
            {
                await _engineLoop;
            }
            catch (OperationCanceledException)
            {
            }

            Icmp.Dispose();
            await _services.DisposeAsync();
            _stop.Dispose();
        }
    }

    /// <summary>Loads targets without polling: the schedule staggers a new target by up to 15 s, so tick before it is due.</summary>
    private static async Task<(Harness H, int Node)> StartAsync(TestDatabase test)
    {
        var node = await test.AddNodeAsync("lab-rtr-hub-01", "192.0.2.11");
        var h = new Harness(test);
        await h.LoadAsync();
        await StatusEngineTests.WaitUntilAsync(() => h.Icmp.InFlightCount == 0);
        return (h, node);
    }

    [Fact]
    public async Task Only_admins_can_poll_and_a_refused_poll_probes_nothing()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (h, node) = await StartAsync(test);
        await using var _ = h;
        var before = h.Probe.CallCount;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Service.PollNowAsync(node, Viewer, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Service.PollNowAsync(node, new ClaimsPrincipal(new ClaimsIdentity()), Ct));
        Assert.Equal(before, h.Probe.CallCount);

        // A refused viewer doesn't use up the node's slot either.
        var outcome = await h.Service.PollNowAsync(node, Admin, Ct);
        Assert.False(outcome.RateLimited);
        Assert.Equal(before + 1, h.Probe.CallCount);
    }

    [Fact]
    public async Task One_manual_poll_per_node_per_20_seconds()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (h, node) = await StartAsync(test);
        await using var _ = h;
        var other = await test.AddNodeAsync("lab-rtr-spoke-01", "192.0.2.21");
        h.Signal.Notify();
        await h.LoadAsync();
        await StatusEngineTests.WaitUntilAsync(() => h.Icmp.InFlightCount == 0);
        var before = h.Probe.CallCount;

        Assert.False((await h.Service.PollNowAsync(node, Admin, Ct)).RateLimited);

        h.Advance(TimeSpan.FromSeconds(19));
        var refused = await h.Service.PollNowAsync(node, Admin, Ct);
        Assert.True(refused.RateLimited);
        Assert.Equal(TimeSpan.FromSeconds(1), refused.RetryAfter);
        Assert.Empty(refused.Sources);
        Assert.Equal(before + 1, h.Probe.CallCount);

        // Another node has its own limit.
        Assert.False((await h.Service.PollNowAsync(other, Admin, Ct)).RateLimited);

        h.Advance(TimeSpan.FromSeconds(1));
        Assert.False((await h.Service.PollNowAsync(node, Admin, Ct)).RateLimited);
    }

    [Fact]
    public async Task A_manual_success_clears_a_failed_state_at_once()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (h, node) = await StartAsync(test);
        await using var _ = h;
        h.Probe.Answer = _ => NoReply;

        // Three failed polls in a row: Critical.
        for (var i = 0; i < 3; i++)
        {
            Assert.False((await h.PollAsync(node)).Sources[0].Ok);
        }

        await StatusEngineTests.WaitUntilAsync(() => h.Cache.StateOf(node) == Severity.Critical);

        h.Probe.Answer = _ => Replies(2);
        var outcome = await h.PollAsync(node);

        Assert.True(outcome.Sources[0].Ok);
        await StatusEngineTests.WaitUntilAsync(() => h.Cache.StateOf(node) == Severity.Normal);
        Assert.All(outcome.Sources.Skip(1), s => Assert.False(s.Polled)); // no SNMP on this node
    }

    [Fact]
    public async Task A_manual_failure_counts_toward_the_consecutive_failure_threshold()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (h, node) = await StartAsync(test);
        await using var _ = h;
        h.Probe.Answer = _ => NoReply;

        await h.PollAsync(node);
        await StatusEngineTests.WaitUntilAsync(() => h.Cache.StateOf(node) == Severity.Warning);
        await h.PollAsync(node);
        await Task.Delay(100, Ct);
        Assert.Equal(Severity.Warning, h.Cache.StateOf(node)); // 2 of 3: still dampened

        await h.PollAsync(node);
        await StatusEngineTests.WaitUntilAsync(() => h.Cache.StateOf(node) == Severity.Critical);
    }

    [Fact]
    public async Task A_manual_poll_does_not_move_the_regular_schedule()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (h, node) = await StartAsync(test);
        await using var _ = h;
        var before = h.Probe.CallCount;

        await h.Service.PollNowAsync(node, Admin, Ct);
        Assert.Equal(before + 1, h.Probe.CallCount);

        // The scheduled poll still fires when it falls due, once.
        h.Advance(TimeSpan.FromSeconds(61));
        await h.LoadAsync();
        await StatusEngineTests.WaitUntilAsync(() => h.Probe.CallCount == before + 2);
    }
}
