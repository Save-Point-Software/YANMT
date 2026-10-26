using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yanmt.Core.Connectivity;
using Yanmt.Core.Monitoring;
using Yanmt.Infrastructure.Collectors.Snmp;
using Yanmt.Infrastructure.Credentials;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Monitoring;
using Yanmt.Infrastructure.Settings;

namespace Yanmt.Infrastructure.Monitors;

/// <summary>One poll job: a node's monitors that share an interval.</summary>
public sealed record MonitorPollTarget(
    int NodeId,
    string Hostname,
    IPAddress Address,
    int Port,
    SnmpV3Credential? Credential,
    string? CredentialError,
    TimeSpan Interval,
    IReadOnlyList<MonitorWork> Works);

/// <summary>A custom-monitor poll of one job. Reading is null when the poll failed.</summary>
public sealed record MonitorPollResult(
    int NodeId,
    DateTime TimestampUtc,
    IReadOnlyList<MonitorWork> Works,
    MonitorReading? Reading,
    FailureCategory? Failure = null,
    string? Error = null)
    : PollResult(NodeId, TimestampUtc);

/// <summary>
/// Polls custom monitors. Each node's monitors are grouped by interval into jobs, and each job is
/// read in as few requests as possible (<see cref="MonitorCollector"/>). Job keys stay stable
/// across reloads so cadence isn't reset by every edit.
/// </summary>
public sealed class MonitorPoller(
    IServiceScopeFactory scopes,
    StatusEngine engine,
    MonitorCollector collector,
    InventoryChangeSignal signal,
    IOptions<PollingOptions> options,
    TimeProvider clock,
    ILogger<MonitorPoller> logger)
    : NodePoller<MonitorPollTarget, SnmpSettings>(scopes, engine, signal, options.Value.SnmpConcurrency, options.Value.ReloadSeconds, clock, logger)
{
    private readonly Dictionary<(int NodeId, int IntervalSeconds), int> _jobKeys = [];

    protected override string Kind => "Monitor";

    protected override async Task<(SnmpSettings Settings, TimeSpan Interval, IReadOnlyDictionary<int, MonitorPollTarget> Targets)> LoadAsync(
        IServiceProvider services, CancellationToken cancellationToken)
    {
        var settings = await services.GetRequiredService<SettingsService>().GetAsync(SettingKeys.Snmp, cancellationToken);
        if (settings.Validate().Count > 0)
        {
            settings = new SnmpSettings();
        }

        var factory = services.GetRequiredService<IDbContextFactory<YanmtDbContext>>();
        var profiles = new SnmpProfileLoader(services.GetRequiredService<CredentialService>());
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var plan = await MonitorPlanner.PlanAsync(db, cancellationToken);
        var nodes = await db.Nodes.AsNoTracking().Where(n => plan.Keys.Contains(n.Id))
            .Select(n => new { n.Id, n.Hostname, n.ManagementIp, n.SnmpProfileId }).ToListAsync(cancellationToken);

        var targets = new Dictionary<int, MonitorPollTarget>();
        foreach (var n in nodes)
        {
            if (!IPAddress.TryParse(n.ManagementIp, out var address))
            {
                continue;
            }

            var profile = await profiles.LoadAsync(n.SnmpProfileId!.Value, cancellationToken);
            foreach (var group in plan[n.Id].GroupBy(w => w.Spec.IntervalSeconds ?? settings.IntervalSeconds))
            {
                if (!_jobKeys.TryGetValue((n.Id, group.Key), out var key))
                {
                    key = _jobKeys.Count + 1;
                    _jobKeys[(n.Id, group.Key)] = key;
                }

                targets[key] = new MonitorPollTarget(n.Id, n.Hostname, address, profile.Loaded?.Port ?? Core.Credentials.CredentialRules.DefaultSnmpPort,
                    profile.Loaded?.Credential, profile.Error, TimeSpan.FromSeconds(group.Key), group.ToList());
            }
        }

        return (settings, TimeSpan.FromSeconds(settings.IntervalSeconds), targets);
    }

    protected override TimeSpan IntervalOf(MonitorPollTarget target, TimeSpan interval) => target.Interval;

    protected override async Task<PollResult> PollAsync(int key, MonitorPollTarget target, SnmpSettings settings, DateTime startedUtc, CancellationToken cancellationToken)
    {
        if (target.Credential is null)
        {
            return new MonitorPollResult(target.NodeId, startedUtc, target.Works, null, FailureCategory.CredentialUnreadable, target.CredentialError);
        }

        var snmpTarget = new SnmpTarget(target.Address, target.Port, target.Credential, TimeSpan.FromMilliseconds(settings.TimeoutMs));
        var reading = await collector.CollectAsync(snmpTarget, target.Works, cancellationToken);
        return new MonitorPollResult(target.NodeId, startedUtc, target.Works, reading);
    }

    protected override PollResult Failed(int key, MonitorPollTarget target, SnmpSettings settings, DateTime startedUtc, Exception exception) =>
        new MonitorPollResult(target.NodeId, startedUtc, target.Works, null,
            exception is SnmpQueryException q ? q.Category : exception is OperationCanceledException ? FailureCategory.Timeout : FailureCategory.Other,
            exception.Message);

    // A few GETs and one table walk, each request possibly retried once; walks of big tables take longer.
    protected override TimeSpan Backstop(SnmpSettings settings) => TimeSpan.FromMilliseconds(settings.TimeoutMs * 30L);
}
