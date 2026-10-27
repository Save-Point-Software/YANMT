using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yanmt.Core.ConfigBackups;
using Yanmt.Core.Credentials;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Collectors.Ssh;
using Yanmt.Infrastructure.ConfigBackups;
using Yanmt.Infrastructure.Credentials;
using Yanmt.Infrastructure.Data.Entities;
using Yanmt.Infrastructure.Monitoring;
using Yanmt.Infrastructure.Settings;
using static Yanmt.Infrastructure.Tests.Monitoring.MonitoringTestSupport;

namespace Yanmt.Infrastructure.Tests.ConfigBackups;

public class ConfigBackupPollerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task SetSshAsync(TestDatabase test, int nodeId, bool sshEnabled, bool withProfile)
    {
        await using var db = test.Open();
        int? profileId = null;
        if (withProfile)
        {
            var profile = new CredentialProfile { Name = $"ssh-{nodeId}-{Guid.NewGuid():N}", Kind = CredentialKind.Ssh, Username = "lab", Port = 22, ProtectedSecrets = [1], CreatedUtc = test.Clock.GetUtcNow().UtcDateTime, UpdatedUtc = test.Clock.GetUtcNow().UtcDateTime };
            db.CredentialProfiles.Add(profile);
            await db.SaveChangesAsync(Ct);
            profileId = profile.Id;
        }

        var node = await db.Nodes.SingleAsync(n => n.Id == nodeId, Ct);
        node.SshEnabled = sshEnabled;
        node.SshProfileId = profileId;
        await db.SaveChangesAsync(Ct);
    }

    private static (ConfigBackupPoller Poller, ServiceProvider Services) Create(TestDatabase test)
    {
        var (engine, _, _) = test.Engine();
        var services = new ServiceCollection()
            .AddSingleton(test.Factory)
            .AddSingleton(test.Clock)
            .AddSingleton<TimeProvider>(test.Clock)
            .AddScoped<SettingsService>()
            .BuildServiceProvider();
        var poller = new ConfigBackupPoller(
            services.GetRequiredService<IServiceScopeFactory>(), engine, new InventoryChangeSignal(),
            Options.Create(new PollingOptions()), test.Clock, NullLogger<ConfigBackupPoller>.Instance);
        return (poller, services);
    }

    private static Task<(ConfigBackupSettings Settings, TimeSpan Interval, IReadOnlyDictionary<int, string> Targets)> LoadAsync(
        ConfigBackupPoller poller, ServiceProvider services) =>
        poller.LoadForTest(services.CreateScope().ServiceProvider, Ct);

    [Fact]
    public async Task Targets_are_enabled_ssh_nodes_with_a_profile_and_the_interval_defaults_to_daily()
    {
        await using var test = await TestDatabase.CreateAsync();
        var ready = await test.AddNodeAsync("lab-rtr-01", "192.0.2.1");
        var noProfile = await test.AddNodeAsync("lab-rtr-02", "192.0.2.2");
        var sshOff = await test.AddNodeAsync("lab-rtr-03", "192.0.2.3");
        var disabled = await test.AddNodeAsync("lab-rtr-04", "192.0.2.4", enabled: false);
        await SetSshAsync(test, ready, true, true);
        await SetSshAsync(test, noProfile, true, false);
        await SetSshAsync(test, sshOff, false, true);
        await SetSshAsync(test, disabled, true, true);
        var (poller, services) = Create(test);

        var (_, interval, targets) = await LoadAsync(poller, services);

        Assert.Equal([ready], targets.Keys);
        Assert.Equal("lab-rtr-01", targets[ready]);
        Assert.Equal(TimeSpan.FromDays(1), interval);
    }

    [Fact]
    public async Task Turning_backups_off_globally_leaves_no_targets()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-01", "192.0.2.1");
        await SetSshAsync(test, node, true, true);
        await new SettingsService(test.Factory, test.Clock).SetAsync(SettingKeys.ConfigBackup, new ConfigBackupSettings { Enabled = false }, Ct);
        var (poller, services) = Create(test);

        var (_, _, targets) = await LoadAsync(poller, services);

        Assert.Empty(targets);
    }

    [Fact]
    public async Task A_failed_scheduled_backup_becomes_a_warning_result()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-01", "192.0.2.1");
        var (poller, _) = Create(test);

        var result = Assert.IsType<BackupPollResult>(
            poller.FailedForTest(node, "lab-rtr-01", new ConfigBackupSettings(), test.Clock.GetUtcNow().UtcDateTime, new InvalidOperationException("boom")));

        Assert.Null(result.Kind);
        Assert.Equal("boom", result.Error);
    }

    [Fact]
    public async Task Sync_switches_off_a_stale_backup_warning_when_the_node_is_no_longer_backed_up()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-01", "192.0.2.1");
        await SetSshAsync(test, node, true, true);
        var (engine, cache, _) = test.Engine();
        await engine.ApplyAsync([new BackupPollResult(node, test.Clock.GetUtcNow().UtcDateTime, null, "refused")], Ct);
        Assert.Equal(Severity.Warning, cache.StateOf(node));
        await engine.SyncAsync(Ct);
        Assert.Equal(Severity.Warning, cache.StateOf(node)); // still a backup target

        await SetSshAsync(test, node, false, true);
        await engine.SyncAsync(Ct);

        Assert.Equal(Severity.Unknown, cache.StateOf(node)); // the source is out of the rollup
    }
}
