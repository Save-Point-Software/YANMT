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

/// <summary>Pings every enabled, ICMP-enabled node on the configured interval.</summary>
public sealed class IcmpPoller(
    IServiceScopeFactory scopes,
    StatusEngine engine,
    IIcmpProbe probe,
    InventoryChangeSignal signal,
    IOptions<PollingOptions> options,
    TimeProvider clock,
    ILogger<IcmpPoller> logger)
    : NodePoller<IPAddress, IcmpSettings>(scopes, engine, signal, options.Value.IcmpConcurrency, options.Value.ReloadSeconds, clock, logger)
{
    protected override string Kind => "ICMP";

    protected override async Task<(IcmpSettings Settings, TimeSpan Interval, IReadOnlyDictionary<int, IPAddress> Targets)> LoadAsync(
        IServiceProvider services, CancellationToken cancellationToken)
    {
        var settings = await services.GetRequiredService<SettingsService>().GetAsync(SettingKeys.Icmp, cancellationToken);
        if (settings.Validate().Count > 0)
        {
            logger.LogWarning("Stored ICMP settings are invalid; using the defaults");
            settings = new IcmpSettings();
        }

        var factory = services.GetRequiredService<IDbContextFactory<YanmtDbContext>>();
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

        return (settings, TimeSpan.FromSeconds(settings.IntervalSeconds), targets);
    }

    protected override async Task<PollResult> PollAsync(int nodeId, IPAddress target, IcmpSettings settings, DateTime startedUtc, CancellationToken cancellationToken)
    {
        var icmp = await probe.ProbeAsync(target, settings.EchoCount, TimeSpan.FromMilliseconds(settings.TimeoutMs),
            TimeSpan.FromMilliseconds(IcmpSettings.EchoSpacingMs), cancellationToken);
        return new IcmpPollResult(nodeId, startedUtc, settings, icmp);
    }

    protected override PollResult Failed(int nodeId, IPAddress target, IcmpSettings settings, DateTime startedUtc, Exception exception)
    {
        // Ping reports loss itself; an exception means something is wrong on this host.
        logger.LogWarning(exception, "ICMP poll of node {NodeId} ({Address}) failed", nodeId, target);
        return new IcmpPollResult(nodeId, startedUtc, settings, null, exception is OperationCanceledException ? "timed out" : exception.Message);
    }

    // Ping honours its own timeout; this is a backstop so a stuck burst can't hold a worker forever.
    protected override TimeSpan Backstop(IcmpSettings settings) => settings.WorstCaseBurst + TimeSpan.FromSeconds(10);
}

/// <summary>Runs the status engine's queue for the lifetime of the host.</summary>
public sealed class StatusEngineService(StatusEngine engine) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => engine.RunAsync(stoppingToken);
}
