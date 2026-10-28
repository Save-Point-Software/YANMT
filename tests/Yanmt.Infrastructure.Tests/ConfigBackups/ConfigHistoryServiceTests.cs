using System.IO.Compression;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yanmt.Core.ConfigBackups;
using Yanmt.Infrastructure.Collectors.Ssh;
using Yanmt.Infrastructure.ConfigBackups;
using Yanmt.Infrastructure.Credentials;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Security;
using Yanmt.Infrastructure.Settings;
using static Yanmt.Infrastructure.Tests.Monitoring.MonitoringTestSupport;

namespace Yanmt.Infrastructure.Tests.ConfigBackups;

public class ConfigHistoryServiceTests
{
    private const string FakeSecret = "$9$FAKEFAKEFAKE$notarealhash";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ClaimsPrincipal Admin = new(new ClaimsIdentity([new Claim(ClaimTypes.Role, YanmtRoles.Admin)], "test", ClaimTypes.Name, ClaimTypes.Role));
    private static readonly ClaimsPrincipal Viewer = new(new ClaimsIdentity([new Claim(ClaimTypes.Role, YanmtRoles.Viewer)], "test", ClaimTypes.Name, ClaimTypes.Role));

    private static string Config(string description) =>
        string.Join('\n', "version 17.9", "hostname lab-rtr-01", $"enable secret 9 {FakeSecret}",
            "interface Loopback0", $" description {description}", " ip address 192.0.2.1 255.255.255.255", "end") + "\n";

    private static async Task<(ConfigHistoryService History, int NodeId, List<ConfigVersionInfo> Versions)> SetupAsync(TestDatabase test)
    {
        var node = await test.AddNodeAsync("lab-rtr-01", "192.0.2.1");
        var (engine, _, _) = test.Engine();
        var settings = new SettingsService(test.Factory, test.Clock);
        var backups = new ConfigBackupService(test.Factory, new CredentialService(test.Factory, test.Protector, test.Clock),
            new ReadOnlySshCollector(), test.Protector, settings, engine, test.Clock, NullLogger<ConfigBackupService>.Instance);
        await backups.RecordAsync(node, Config("first"), BackupTrigger.Manual, Ct);
        await backups.RecordAsync(node, Config("second"), BackupTrigger.Manual, Ct);
        var audit = new AuditLogService(test.Factory, new SystemUser(), test.Clock);
        var history = new ConfigHistoryService(test.Factory, backups, settings, audit);
        return (history, node, await history.ListAsync(node, Ct));
    }

    private static async Task<List<string>> RevealAuditsAsync(TestDatabase test)
    {
        await using var db = test.Open();
        return await db.AuditEntries.Where(a => a.EntityType == "ConfigVersion").Select(a => a.Action).ToListAsync(Ct);
    }

    [Fact]
    public async Task Viewer_asking_to_reveal_gets_masked_text_and_no_audit()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (history, nodeId, versions) = await SetupAsync(test);

        var text = await history.GetTextAsync(nodeId, versions[0].Id, Viewer, reveal: true, Ct);

        Assert.NotNull(text);
        Assert.False(text.Revealed);
        Assert.DoesNotContain(FakeSecret, text.Text);
        Assert.Contains(ConfigSecretMasker.Mask, text.Text);
        Assert.Empty(await RevealAuditsAsync(test));
    }

    [Fact]
    public async Task Admin_reveal_returns_secret_and_is_audited_without_the_text()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (history, nodeId, versions) = await SetupAsync(test);

        var text = await history.GetTextAsync(nodeId, versions[0].Id, Admin, reveal: true, Ct);

        Assert.True(text!.Revealed);
        Assert.Contains(FakeSecret, text.Text);
        Assert.Equal(["RevealConfig"], await RevealAuditsAsync(test));
        await using var db = test.Open();
        Assert.DoesNotContain(FakeSecret, string.Concat(await db.AuditEntries.Select(a => a.Changes).ToListAsync(Ct)));
    }

    [Fact]
    public async Task Admin_without_reveal_is_masked_and_not_audited()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (history, nodeId, versions) = await SetupAsync(test);

        var text = await history.GetTextAsync(nodeId, versions[0].Id, Admin, reveal: false, Ct);

        Assert.DoesNotContain(FakeSecret, text!.Text);
        Assert.Empty(await RevealAuditsAsync(test));
    }

    [Fact]
    public async Task Version_of_another_node_is_not_found()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (history, nodeId, versions) = await SetupAsync(test);

        Assert.Null(await history.GetTextAsync(nodeId + 1000, versions[0].Id, Admin, reveal: true, Ct));
        Assert.Null(await history.DiffAsync(nodeId + 1000, versions[1].Id, versions[0].Id, Admin, reveal: true, Ct));
        Assert.Empty(await RevealAuditsAsync(test));
    }

    [Fact]
    public async Task Diff_shows_the_changed_line_in_both_layouts()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (history, nodeId, versions) = await SetupAsync(test);

        var diff = await history.DiffAsync(nodeId, versions[1].Id, versions[0].Id, Viewer, reveal: false, Ct);

        Assert.NotNull(diff);
        Assert.Contains(diff.Unified, l => l.Kind == DiffLineKind.Removed && l.Text.Contains("first"));
        Assert.Contains(diff.Unified, l => l.Kind == DiffLineKind.Added && l.Text.Contains("second"));
        Assert.Contains(diff.SideBySide, r => r.Old.Text.Contains("first") && r.New.Text.Contains("second"));
        Assert.All(diff.Unified.Concat(diff.SideBySide.SelectMany(r => new[] { r.Old, r.New })), l => Assert.DoesNotContain(FakeSecret, l.Text));
    }

    [Fact]
    public async Task Zip_is_masked_for_viewers_even_with_secrets_flag_and_audited_for_admin_opt_in()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (history, nodeId, _) = await SetupAsync(test);

        using var viewerZip = new MemoryStream();
        Assert.True(await history.WriteZipAsync(nodeId, "lab-rtr-01", Viewer, includeSecrets: true, viewerZip, Ct));
        Assert.DoesNotContain(FakeSecret, ReadAll(viewerZip));
        Assert.Empty(await RevealAuditsAsync(test));

        using var adminZip = new MemoryStream();
        Assert.True(await history.WriteZipAsync(nodeId, "lab-rtr-01", Admin, includeSecrets: true, adminZip, Ct));
        Assert.Contains(FakeSecret, ReadAll(adminZip));
        Assert.Equal(["ExportConfigWithSecrets"], await RevealAuditsAsync(test));
    }

    [Fact]
    public async Task Zip_has_one_file_per_version()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (history, nodeId, _) = await SetupAsync(test);

        using var stream = new MemoryStream();
        await history.WriteZipAsync(nodeId, "lab/rtr 01", Viewer, includeSecrets: false, stream, Ct);
        stream.Position = 0;
        using var zip = new ZipArchive(stream);

        Assert.Equal(2, zip.Entries.Count);
        Assert.All(zip.Entries, e => Assert.StartsWith("lab_rtr_01/", e.FullName));
    }

    private static string ReadAll(MemoryStream stream)
    {
        stream.Position = 0;
        using var zip = new ZipArchive(stream);
        return string.Concat(zip.Entries.Select(e => new StreamReader(e.Open()).ReadToEnd()));
    }
}
