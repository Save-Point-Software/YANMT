using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yanmt.Core.ConfigBackups;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Monitoring;
using Yanmt.Infrastructure.Settings;

namespace Yanmt.Infrastructure.ConfigBackups;

/// <summary>
/// Backs up the running-config of every enabled, SSH-enabled node that has an SSH profile, on the
/// configured interval (default daily). Each run is a <see cref="BackupTrigger.Scheduled"/> backup, so
/// a failure sets the node's Backup source to Warning. Like every poller it only submits a result;
/// <see cref="StatusEngine"/> writes the state. When the global Enabled setting is off there are no targets.
/// </summary>
public sealed class ConfigBackupPoller(
    IServiceScopeFactory scopes,
    StatusEngine engine,
    InventoryChangeSignal signal,
    IOptions<PollingOptions> options,
    TimeProvider clock,
    ILogger<ConfigBackupPoller> logger)
    : NodePoller<string, ConfigBackupSettings>(scopes, engine, signal, options.Value.BackupConcurrency, options.Value.ReloadSeconds, clock, logger)
{
    private readonly IServiceScopeFactory _scopes = scopes;

    protected override string Kind => "Config backup";

    protected override async Task<(ConfigBackupSettings Settings, TimeSpan Interval, IReadOnlyDictionary<int, string> Targets)> LoadAsync(
        IServiceProvider services, CancellationToken cancellationToken)
    {
        var settings = await services.GetRequiredService<SettingsService>().GetAsync(SettingKeys.ConfigBackup, cancellationToken);
        if (settings.Validate().Count > 0)
        {
            logger.LogWarning("Stored config backup settings are invalid; using the defaults");
            settings = new ConfigBackupSettings();
        }

        var targets = new Dictionary<int, string>();
        if (settings.Enabled)
        {
            var factory = services.GetRequiredService<IDbContextFactory<YanmtDbContext>>();
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var nodes = await db.Nodes.AsNoTracking()
                .Where(n => n.Enabled && n.SshEnabled && n.SshProfileId != null)
                .Select(n => new { n.Id, n.Hostname })
                .ToListAsync(cancellationToken);
            foreach (var n in nodes)
            {
                targets[n.Id] = n.Hostname;
            }
        }

        return (settings, TimeSpan.FromSeconds(settings.IntervalSeconds), targets);
    }

    protected override async Task<PollResult> PollAsync(int nodeId, string hostname, ConfigBackupSettings settings, DateTime startedUtc, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ConfigBackupService>();
        var attempt = await service.AttemptAsync(nodeId, BackupTrigger.Scheduled, cancellationToken);

        // A scheduled attempt always yields a result unless the node vanished mid-run.
        return attempt.Result ?? new BackupPollResult(nodeId, startedUtc, null, attempt.Outcome.Message);
    }

    protected override PollResult Failed(int nodeId, string hostname, ConfigBackupSettings settings, DateTime startedUtc, Exception exception)
    {
        logger.LogWarning(exception, "Config backup of {Hostname} (node {NodeId}) failed", hostname, nodeId);
        return new BackupPollResult(nodeId, startedUtc, null,
            exception is OperationCanceledException ? "the backup didn't finish in time" : exception.Message);
    }

    // Connect plus a slow running-config print, with slack. The SSH layer has its own timeouts; this is a backstop.
    protected override TimeSpan Backstop(ConfigBackupSettings settings) =>
        ConfigBackupService.SshConnectTimeout + ConfigBackupService.SshCommandTimeout + TimeSpan.FromSeconds(30);

    internal Task<(ConfigBackupSettings Settings, TimeSpan Interval, IReadOnlyDictionary<int, string> Targets)> LoadForTest(
        IServiceProvider services, CancellationToken cancellationToken) => LoadAsync(services, cancellationToken);

    internal PollResult FailedForTest(int nodeId, string hostname, ConfigBackupSettings settings, DateTime startedUtc, Exception exception) =>
        Failed(nodeId, hostname, settings, startedUtc, exception);
}
