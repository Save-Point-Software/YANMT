using System.Collections.Concurrent;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yanmt.Core.Monitoring;
using Yanmt.Infrastructure.Collectors.Icmp;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Settings;

namespace Yanmt.Infrastructure.Monitoring;

/// <summary>
/// Pings every enabled, ICMP-enabled node on the configured interval. A 1-second tick hands due
/// nodes to a bounded set of workers; a node already in flight is never started twice, and a slow
/// or dead node only ever occupies one worker for one burst.
/// </summary>
public sealed class IcmpPoller(
    IServiceScopeFactory scopes,
    StatusEngine engine,
    IIcmpProbe probe,
    InventoryChangeSignal signal,
    IOptions<PollingOptions> options,
    TimeProvider clock,
    ILogger<IcmpPoller> logger) : BackgroundService
{
    private readonly PollSchedule _schedule = new();
    private readonly ConcurrentDictionary<int, byte> _inFlight = new();
    private readonly SemaphoreSlim _workers = new(Math.Max(1, options.Value.IcmpConcurrency));
    private Dictionary<int, IPAddress> _targets = [];
    private IcmpSettings _settings = new();
    private long _loadedVersion = -1;
    private DateTimeOffset _nextReload = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "ICMP poller tick failed; retrying on the next tick");
                _nextReload = DateTimeOffset.MinValue; // reload from scratch next time
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var version = signal.Version;
        if (version != _loadedVersion || now >= _nextReload)
        {
            await ReloadAsync(now, cancellationToken);
            _loadedVersion = version;
            _nextReload = now + TimeSpan.FromSeconds(Math.Max(10, options.Value.ReloadSeconds));
        }

        foreach (var nodeId in _schedule.TakeDue(now))
        {
            if (_targets.TryGetValue(nodeId, out var address) && _inFlight.TryAdd(nodeId, 0))
            {
                _ = PollAsync(nodeId, address, _settings, cancellationToken);
            }
        }
    }

    private async Task ReloadAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        _settings = await scope.ServiceProvider.GetRequiredService<SettingsService>().GetAsync(SettingKeys.Icmp, cancellationToken);
        if (_settings.Validate().Count > 0)
        {
            logger.LogWarning("Stored ICMP settings are invalid; using the defaults");
            _settings = new IcmpSettings();
        }

        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<YanmtDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var nodes = await db.Nodes.AsNoTracking()
            .Where(n => n.Enabled && n.IcmpEnabled)
            .Select(n => new { n.Id, n.Hostname, n.ManagementIp })
            .ToListAsync(cancellationToken);

        var targets = new Dictionary<int, IPAddress>();
        foreach (var n in nodes)
        {
            if (IPAddress.TryParse(n.ManagementIp, out var address))
            {
                targets[n.Id] = address;
            }
            else
            {
                logger.LogWarning("Node {Hostname} has an unparsable management IP {Ip}; not pinging it", n.Hostname, n.ManagementIp);
            }
        }

        _targets = targets;
        _schedule.SetTargets(targets.Keys, TimeSpan.FromSeconds(_settings.IntervalSeconds), now);
        await engine.SyncAsync(cancellationToken);
        logger.LogInformation("ICMP polling {Count} node(s) every {Interval} s", targets.Count, _settings.IntervalSeconds);
    }

    private async Task PollAsync(int nodeId, IPAddress address, IcmpSettings settings, CancellationToken cancellationToken)
    {
        IcmpPollResult result;
        try
        {
            await _workers.WaitAsync(cancellationToken);
            try
            {
                var started = clock.GetUtcNow().UtcDateTime;

                // Ping honours its own timeout; this is a backstop so a stuck burst can't hold a worker forever.
                using var guard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                guard.CancelAfter(settings.WorstCaseBurst + TimeSpan.FromSeconds(10));
                try
                {
                    var icmp = await probe.ProbeAsync(address, settings.EchoCount, TimeSpan.FromMilliseconds(settings.TimeoutMs),
                        TimeSpan.FromMilliseconds(IcmpSettings.EchoSpacingMs), guard.Token);
                    result = new IcmpPollResult(nodeId, started, settings, icmp);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "ICMP poll of node {NodeId} ({Address}) failed", nodeId, address);
                    result = new IcmpPollResult(nodeId, started, settings, null, ex is OperationCanceledException ? "timed out" : ex.Message);
                }
            }
            finally
            {
                _workers.Release();
            }

            await engine.SubmitAsync(result, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ICMP poll of node {NodeId} could not be queued", nodeId);
        }
        finally
        {
            _inFlight.TryRemove(nodeId, out _);
        }
    }

    public override void Dispose()
    {
        _workers.Dispose();
        base.Dispose();
    }
}

/// <summary>Runs the status engine's queue for the lifetime of the host.</summary>
public sealed class StatusEngineService(StatusEngine engine) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => engine.RunAsync(stoppingToken);
}
