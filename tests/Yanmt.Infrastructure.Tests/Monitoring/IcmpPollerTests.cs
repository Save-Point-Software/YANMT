using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yanmt.Core.Monitoring;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Monitoring;
using Yanmt.Infrastructure.Settings;
using static Yanmt.Infrastructure.Tests.Monitoring.MonitoringTestSupport;

namespace Yanmt.Infrastructure.Tests.Monitoring;

public class IcmpPollerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        private readonly Task _engineLoop;

        public Harness(TestDatabase test, int concurrency = 8)
        {
            Test = test;
            var services = new ServiceCollection();
            services.AddSingleton(test.Factory);
            services.AddSingleton<TimeProvider>(test.Clock);
            services.AddSingleton(Signal);
            services.AddScoped<SettingsService>();
            _services = services.BuildServiceProvider();

            (Engine, Cache, _) = test.Engine();
            Poller = new IcmpPoller(_services.GetRequiredService<IServiceScopeFactory>(), Engine, Probe, Signal,
                Options.Create(new PollingOptions { IcmpConcurrency = concurrency }), test.Clock, NullLogger<IcmpPoller>.Instance);
            _engineLoop = Engine.RunAsync(_stop.Token);
        }

        public TestDatabase Test { get; }
        public InventoryChangeSignal Signal { get; } = new();
        public FakeIcmpProbe Probe { get; } = new();
        public StatusEngine Engine { get; }
        public StatusCache Cache { get; }
        public IcmpPoller Poller { get; }

        public Task TickAsync() => Poller.TickAsync(_stop.Token);

        public void Advance(TimeSpan by) => Test.Clock.Now += by;

        public async ValueTask DisposeAsync()
        {
            Probe.Hold?.TrySetResult();
            await _stop.CancelAsync();
            try
            {
                await _engineLoop;
            }
            catch (OperationCanceledException)
            {
            }

            Poller.Dispose();
            await _services.DisposeAsync();
            _stop.Dispose();
        }
    }

    [Fact]
    public async Task Polls_enabled_icmp_nodes_and_feeds_the_engine()
    {
        await using var test = await TestDatabase.CreateAsync();
        var up = await test.AddNodeAsync("lab-rtr-hub-01", "192.0.2.11");
        var down = await test.AddNodeAsync("lab-rtr-spoke-01", "192.0.2.21");
        await test.AddNodeAsync("lab-rtr-off-01", "192.0.2.31", enabled: false);
        await test.AddNodeAsync("lab-sw-noping-01", "192.0.2.32", icmp: false);
        await using var h = new Harness(test);
        h.Probe.Answer = ip => ip.Equals(IPAddress.Parse("192.0.2.21")) ? NoReply : Replies(4);

        // Two targets: staggered 0 s and 7.5 s apart.
        await h.TickAsync();
        h.Advance(TimeSpan.FromSeconds(8));
        await h.TickAsync();

        await StatusEngineTests.WaitUntilAsync(() => h.Cache.Get(up)?.Sources.Count == 1 && h.Cache.Get(down)?.Sources.Count == 1);
        Assert.Equal(["192.0.2.11", "192.0.2.21"], h.Probe.Calls.Select(c => c.ToString()).Order());
        Assert.Equal(Severity.Normal, h.Cache.StateOf(up));
        Assert.Equal(Severity.Warning, h.Cache.StateOf(down)); // first failure: Warning until 3 in a row
    }

    [Fact]
    public async Task A_node_still_in_flight_is_not_started_again()
    {
        await using var test = await TestDatabase.CreateAsync();
        await test.AddNodeAsync("lab-rtr-hub-01", "192.0.2.11");
        await using var h = new Harness(test);
        h.Probe.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await h.TickAsync();
        h.Advance(TimeSpan.FromSeconds(61)); // due again, but the first burst hasn't finished
        await h.TickAsync();

        Assert.Equal(1, h.Probe.CallCount);
        h.Probe.Hold.SetResult();
    }

    [Fact]
    public async Task Inventory_signal_reloads_targets_and_settings()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-hub-01", "192.0.2.11");
        await using var h = new Harness(test);
        await h.TickAsync();
        await StatusEngineTests.WaitUntilAsync(() => h.Probe.CallCount == 1);

        // Turn ICMP off for the node and shorten the interval; signal as NodeService/SettingsService do.
        await using (var db = test.Open())
        {
            (await db.Nodes.SingleAsync(n => n.Id == node, Ct)).IcmpEnabled = false;
            await db.SaveChangesAsync(Ct);
        }

        h.Signal.Notify();
        h.Advance(TimeSpan.FromSeconds(61));
        await h.TickAsync();

        Assert.Equal(1, h.Probe.CallCount);
        Assert.Equal(Severity.Unknown, h.Cache.StateOf(node));
    }

    [Fact]
    public async Task Interval_comes_from_settings()
    {
        await using var test = await TestDatabase.CreateAsync();
        await test.AddNodeAsync("lab-rtr-hub-01", "192.0.2.11");
        await using var h = new Harness(test);
        await new SettingsService(test.Factory, test.Clock).SetAsync(SettingKeys.Icmp, new IcmpSettings { IntervalSeconds = 20, TimeoutMs = 1000 }, Ct);

        await h.TickAsync();
        h.Advance(TimeSpan.FromSeconds(21));
        await StatusEngineTests.WaitUntilAsync(() => h.Probe.CallCount == 1);
        await h.TickAsync();

        await StatusEngineTests.WaitUntilAsync(() => h.Probe.CallCount == 2);
    }

    [Fact]
    public async Task A_throwing_probe_becomes_an_unknown_result()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-hub-01", "192.0.2.11");
        await using var h = new Harness(test);
        h.Probe.Answer = _ => throw new InvalidOperationException("no ICMP on this host");

        await h.TickAsync();

        await StatusEngineTests.WaitUntilAsync(() => h.Cache.Get(node)?.Sources.Count == 1);
        Assert.Equal(("poll failed", Severity.Unknown), (h.Cache.Get(node)!.Sources[0].ValueText, h.Cache.StateOf(node)));
    }

    [Fact]
    public async Task Node_and_settings_saves_raise_the_signal()
    {
        await using var test = await TestDatabase.CreateAsync();
        var signal = new InventoryChangeSignal();
        var nodes = new Yanmt.Infrastructure.Inventory.NodeService(test.Factory, test.Clock, signal);
        var settings = new SettingsService(test.Factory, test.Clock, signal);

        var created = await nodes.CreateAsync(new Yanmt.Infrastructure.Inventory.NodeEditModel { Hostname = "lab-rtr-hub-01", ManagementIp = "192.0.2.11" }, Ct);
        Assert.True(created.Ok);
        Assert.Equal(1, signal.Version);
        await settings.SetAsync(SettingKeys.Icmp, new IcmpSettings(), Ct);
        Assert.Equal(2, signal.Version);
        await nodes.DeleteAsync(created.Id!.Value, Ct);
        Assert.Equal(3, signal.Version);
    }
}
