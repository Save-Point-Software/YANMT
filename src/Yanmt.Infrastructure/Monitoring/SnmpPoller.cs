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
using Yanmt.Infrastructure.Security;
using Yanmt.Infrastructure.Settings;

namespace Yanmt.Infrastructure.Monitoring;

/// <summary>
/// One node's SNMP polling target. The credential is decrypted at reload and held in memory only
/// (its ToString is masked); <see cref="CredentialError"/> is set instead when it can't be read.
/// </summary>
public sealed record SnmpPollTarget(
    string Hostname,
    IPAddress Address,
    int Port,
    SnmpV3Credential? Credential,
    string? CredentialError,
    Thresholds Cpu,
    Thresholds Memory);

/// <summary>Polls the default stats (system group, uptime, CPU, memory) on every SNMP-enabled node with a profile.</summary>
public sealed class SnmpPoller(
    IServiceScopeFactory scopes,
    StatusEngine engine,
    DefaultStatsCollector collector,
    InventoryChangeSignal signal,
    IOptions<PollingOptions> options,
    TimeProvider clock,
    ILogger<SnmpPoller> logger)
    : NodePoller<SnmpPollTarget, SnmpSettings>(scopes, engine, signal, options.Value.SnmpConcurrency, options.Value.ReloadSeconds, clock, logger)
{
    protected override string Kind => "SNMP";

    protected override async Task<(SnmpSettings Settings, TimeSpan Interval, IReadOnlyDictionary<int, SnmpPollTarget> Targets)> LoadAsync(
        IServiceProvider services, CancellationToken cancellationToken)
    {
        var settings = await services.GetRequiredService<SettingsService>().GetAsync(SettingKeys.Snmp, cancellationToken);
        if (settings.Validate().Count > 0)
        {
            logger.LogWarning("Stored SNMP settings are invalid; using the defaults");
            settings = new SnmpSettings();
        }

        var factory = services.GetRequiredService<IDbContextFactory<YanmtDbContext>>();
        var credentials = services.GetRequiredService<CredentialService>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var nodes = await db.Nodes.AsNoTracking()
            .Where(n => n.Enabled && n.SnmpEnabled && n.SnmpProfileId != null)
            .Select(n => new { n.Id, n.Hostname, n.ManagementIp, n.SnmpProfileId, n.CpuWarningPercent, n.CpuCriticalPercent, n.MemoryWarningPercent, n.MemoryCriticalPercent })
            .ToListAsync(cancellationToken);

        // Nodes usually share a few profiles: decrypt each once per reload.
        var profiles = new Dictionary<int, ((SnmpV3Credential Credential, int Port)? Loaded, string? Error)>();
        var targets = new Dictionary<int, SnmpPollTarget>();
        foreach (var n in nodes)
        {
            if (!IPAddress.TryParse(n.ManagementIp, out var address))
            {
                logger.LogWarning("Node {Hostname} has an unparsable management IP {Ip}; not polling SNMP", n.Hostname, n.ManagementIp);
                continue;
            }

            var profileId = n.SnmpProfileId!.Value;
            if (!profiles.TryGetValue(profileId, out var profile))
            {
                try
                {
                    var loaded = await credentials.LoadSnmpAsync(profileId, cancellationToken);
                    profile = (loaded, loaded is null ? "The assigned SNMPv3 profile no longer exists." : null);
                }
                catch (SecretUnreadableException)
                {
                    profile = (null, "The SNMPv3 profile's passwords can't be decrypted on this server; re-enter them under Credentials.");
                }

                profiles[profileId] = profile;
            }

            targets[n.Id] = new SnmpPollTarget(
                n.Hostname,
                address,
                profile.Loaded?.Port ?? Core.Credentials.CredentialRules.DefaultSnmpPort,
                profile.Loaded?.Credential,
                profile.Error,
                settings.Cpu.Override(n.CpuWarningPercent, n.CpuCriticalPercent),
                settings.Memory.Override(n.MemoryWarningPercent, n.MemoryCriticalPercent));
        }

        return (settings, TimeSpan.FromSeconds(settings.IntervalSeconds), targets);
    }

    protected override async Task<PollResult> PollAsync(int nodeId, SnmpPollTarget target, SnmpSettings settings, DateTime startedUtc, CancellationToken cancellationToken)
    {
        if (target.Credential is null)
        {
            return new SnmpPollResult(nodeId, startedUtc, target.Cpu, target.Memory, null, FailureCategory.CredentialUnreadable, target.CredentialError);
        }

        var snmpTarget = new SnmpTarget(target.Address, target.Port, target.Credential, TimeSpan.FromMilliseconds(settings.TimeoutMs));
        var reading = await collector.CollectAsync(snmpTarget, cancellationToken);
        return new SnmpPollResult(nodeId, startedUtc, target.Cpu, target.Memory, reading);
    }

    protected override PollResult Failed(int nodeId, SnmpPollTarget target, SnmpSettings settings, DateTime startedUtc, Exception exception) =>
        exception switch
        {
            SnmpQueryException q => new SnmpPollResult(nodeId, startedUtc, target.Cpu, target.Memory, null, q.Category, q.Message),
            OperationCanceledException => new SnmpPollResult(nodeId, startedUtc, target.Cpu, target.Memory, null, FailureCategory.Timeout,
                "The poll took too long and was stopped."),
            _ => Unexpected(nodeId, target, startedUtc, exception),
        };

    private SnmpPollResult Unexpected(int nodeId, SnmpPollTarget target, DateTime startedUtc, Exception exception)
    {
        logger.LogWarning(exception, "SNMP poll of {Hostname} failed unexpectedly", target.Hostname);
        return new SnmpPollResult(nodeId, startedUtc, target.Cpu, target.Memory, null, FailureCategory.Other, exception.Message);
    }

    // A GET plus up to two walks of a few requests each, each request possibly retried once.
    protected override TimeSpan Backstop(SnmpSettings settings) => TimeSpan.FromMilliseconds(settings.TimeoutMs * 12L);
}
