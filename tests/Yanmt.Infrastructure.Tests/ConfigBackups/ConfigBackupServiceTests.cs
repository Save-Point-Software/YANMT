using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yanmt.Core.ConfigBackups;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Collectors.Ssh;
using Yanmt.Infrastructure.ConfigBackups;
using Yanmt.Infrastructure.Credentials;
using Yanmt.Infrastructure.Data.Entities;
using Yanmt.Infrastructure.Monitoring;
using Yanmt.Infrastructure.Settings;
using static Yanmt.Infrastructure.Tests.Monitoring.MonitoringTestSupport;

namespace Yanmt.Infrastructure.Tests.ConfigBackups;

public class ConfigBackupServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Config(string hostname = "lab-rtr-01", string stamp = "10:15:42 UTC Mon Jan 5 2026") =>
        string.Join('\n',
            "Building configuration...",
            "",
            "Current configuration : 1234 bytes",
            "!",
            $"! Last configuration change at {stamp} by labadmin",
            "version 17.9",
            $"hostname {hostname}",
            "enable secret 9 $9$FAKEFAKEFAKE$notarealhash",
            "interface Loopback0",
            " ip address 192.0.2.1 255.255.255.255",
            "end") + "\n";

    private static (ConfigBackupService Service, StatusEngine Engine, StatusCache Cache) Create(TestDatabase test)
    {
        var (engine, cache, _) = test.Engine();
        var service = new ConfigBackupService(
            test.Factory,
            new CredentialService(test.Factory, test.Protector, test.Clock),
            new ReadOnlySshCollector(),
            test.Protector,
            new SettingsService(test.Factory, test.Clock),
            engine,
            test.Clock,
            NullLogger<ConfigBackupService>.Instance);
        return (service, engine, cache);
    }

    private static async Task<List<ConfigVersion>> VersionsAsync(TestDatabase test, int nodeId)
    {
        await using var db = test.Open();
        return await db.ConfigVersions.Where(v => v.NodeId == nodeId).OrderBy(v => v.Id).ToListAsync(Ct);
    }

    [Fact]
    public async Task First_capture_is_stored_encrypted_and_reads_back()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-01", "192.0.2.1");
        var (service, _, _) = Create(test);

        var outcome = await service.RecordAsync(node, Config(), Ct);

        Assert.True(outcome.Success);
        Assert.Equal(ConfigChangeKind.Initial, outcome.Kind);
        var version = Assert.Single(await VersionsAsync(test, node));
        Assert.Equal(64, version.ContentHash.Length);
        Assert.DoesNotContain("hostname", Encoding.UTF8.GetString(version.ProtectedRaw)); // stored encrypted
        Assert.Equal(Config(), await service.ReadRawAsync(version.Id, Ct)); // raw, unaltered
    }

    [Fact]
    public async Task Volatile_only_differences_store_nothing_new()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-01", "192.0.2.1");
        var (service, _, _) = Create(test);

        await service.RecordAsync(node, Config(), Ct);
        var second = await service.RecordAsync(node, Config(stamp: "23:59:59 UTC Tue Jan 6 2026"), Ct);

        Assert.Equal(ConfigChangeKind.Unchanged, second.Kind);
        Assert.Single(await VersionsAsync(test, node));
    }

    [Fact]
    public async Task Real_change_stores_a_second_version()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-01", "192.0.2.1");
        var (service, _, _) = Create(test);

        await service.RecordAsync(node, Config(), Ct);
        test.Clock.Advance(TimeSpan.FromDays(1));
        var second = await service.RecordAsync(node, Config(hostname: "lab-rtr-renamed"), Ct);

        Assert.Equal(ConfigChangeKind.Changed, second.Kind);
        var versions = await VersionsAsync(test, node);
        Assert.Equal(2, versions.Count);
        Assert.NotEqual(versions[0].ContentHash, versions[1].ContentHash);
        Assert.Contains("lab-rtr-renamed", await service.ReadRawAsync(versions[1].Id, Ct));
    }

    [Fact]
    public async Task Extra_ignore_patterns_from_settings_apply()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-01", "192.0.2.1");
        var (service, _, _) = Create(test);
        await new SettingsService(test.Factory, test.Clock).SetAsync(
            SettingKeys.ConfigBackup, new ConfigBackupSettings { NormalizationPatterns = ["^hostname "] }, Ct);

        await service.RecordAsync(node, Config(), Ct);
        var second = await service.RecordAsync(node, Config(hostname: "lab-rtr-renamed"), Ct);

        Assert.Equal(ConfigChangeKind.Unchanged, second.Kind);
    }

    [Fact]
    public async Task Invalid_ignore_pattern_fails_the_backup_and_stores_nothing()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-01", "192.0.2.1");
        var (service, _, _) = Create(test);
        await new SettingsService(test.Factory, test.Clock).SetAsync(
            SettingKeys.ConfigBackup, new ConfigBackupSettings { NormalizationPatterns = ["(unclosed"] }, Ct);

        var outcome = await service.RecordAsync(node, Config(), Ct);

        Assert.False(outcome.Success);
        Assert.Empty(await VersionsAsync(test, node));
    }

    [Fact]
    public async Task Backing_up_a_node_without_an_ssh_profile_fails_with_not_configured()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-01", "192.0.2.1");
        var (service, _, _) = Create(test);

        var outcome = await service.BackupNodeAsync(node, Ct);

        Assert.False(outcome.Success);
        Assert.Equal(Yanmt.Core.Connectivity.FailureCategory.NotConfigured, outcome.Failure);
        Assert.Empty(await VersionsAsync(test, node));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("% Invalid input detected at '^' marker.\n", false)]
    [InlineData("version 17.9\nhostname x\n", false)]
    [InlineData("version 17.9\nhostname x\nend\n", true)]
    [InlineData("version 17.9\r\nhostname x\r\nend\r\n\r\n", true)]
    public void Output_check_rejects_errors_and_truncated_output(string output, bool ok) =>
        Assert.Equal(ok, ConfigOutputCheck.Problem(output) is null);

    [Fact]
    public async Task Engine_sets_backup_source_to_warning_on_failure_and_back_to_normal()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-01", "192.0.2.1");
        var (_, engine, cache) = Create(test);
        DateTime Next() => (test.Clock.Now = test.Clock.Now.AddMinutes(1)).UtcDateTime;

        await engine.ApplyAsync([new BackupPollResult(node, Next(), ConfigChangeKind.Initial)], Ct);
        Assert.Equal(Severity.Normal, cache.StateOf(node));
        await engine.ApplyAsync([new BackupPollResult(node, Next(), null, "refused the TCP connection")], Ct);
        Assert.Equal(Severity.Warning, cache.StateOf(node));
        await engine.ApplyAsync([new BackupPollResult(node, Next(), ConfigChangeKind.Unchanged)], Ct);
        Assert.Equal(Severity.Normal, cache.StateOf(node));

        await using var db = test.Open();
        var events = await db.Events.OrderBy(e => e.Id).ToListAsync(Ct);
        Assert.Equal([(Severity.Normal, Severity.Warning), (Severity.Warning, Severity.Normal)], events.Select(e => (e.OldState!.Value, e.NewState)));
        Assert.All(events, e => Assert.Equal(StatusSourceType.Backup, e.Source));
        Assert.Contains("refused the TCP connection", events[0].Message);
    }

    [Fact]
    public async Task Engine_records_an_event_for_a_config_change_but_not_for_the_first_backup()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-01", "192.0.2.1");
        var (_, engine, cache) = Create(test);
        DateTime Next() => (test.Clock.Now = test.Clock.Now.AddMinutes(1)).UtcDateTime;

        await engine.ApplyAsync([new BackupPollResult(node, Next(), ConfigChangeKind.Initial)], Ct);
        await using (var db = test.Open())
        {
            Assert.Empty(await db.Events.ToListAsync(Ct));
        }

        await engine.ApplyAsync([new BackupPollResult(node, Next(), ConfigChangeKind.Changed)], Ct);

        await using var db2 = test.Open();
        var change = Assert.Single(await db2.Events.ToListAsync(Ct));
        Assert.Equal((StatusSourceType.Backup, Severity.Normal, Severity.Normal), (change.Source, change.OldState!.Value, change.NewState));
        Assert.Contains("running-config changed", change.Message);
        Assert.Equal("config changed", cache.Get(node)!.Source(StatusSourceType.Backup)!.ValueText);
    }

    [Fact]
    public async Task Versions_are_deleted_with_their_node()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-01", "192.0.2.1");
        var (service, _, _) = Create(test);
        await service.RecordAsync(node, Config(), Ct);

        await using (var db = test.Open())
        {
            // SQLite foreign keys are enabled by SqlitePragmaInterceptor, so the cascade applies.
            await db.Nodes.Where(n => n.Id == node).ExecuteDeleteAsync(Ct);
        }

        Assert.Empty(await VersionsAsync(test, node));
    }
}
