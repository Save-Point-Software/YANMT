using Microsoft.EntityFrameworkCore;
using Yanmt.Core.Credentials;
using Yanmt.Core.Inventory;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Data.Entities;
using Yanmt.Infrastructure.Credentials;
using Yanmt.Infrastructure.Inventory;

namespace Yanmt.Infrastructure.Tests.Inventory;

public class NodeServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NodeEditModel Model(string hostname = "rtr-hub-01", string ip = "192.0.2.1", string? tags = null) => new()
    {
        Hostname = hostname,
        ManagementIp = ip,
        Site = "HQ",
        Tags = tags,
        Icon = NodeIconType.Router,
        SnmpEnabled = true,
    };

    [Fact]
    public async Task Create_then_get_round_trips_and_normalizes_ip()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = new NodeService(test.Factory, test.Clock);

        var result = await svc.CreateAsync(Model(ip: "2001:DB8::1", tags: "core; dmvpn"), Ct);
        Assert.True(result.Ok);

        var loaded = await svc.GetForEditAsync(result.Id!.Value, Ct);
        Assert.NotNull(loaded);
        Assert.Equal("2001:db8::1", loaded.ManagementIp);
        Assert.Equal("core; dmvpn", loaded.Tags);
        Assert.True(loaded.SnmpEnabled);
    }

    [Fact]
    public async Task Duplicate_hostname_is_rejected_case_insensitively()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = new NodeService(test.Factory, test.Clock);
        await svc.CreateAsync(Model("rtr-hub-01"), Ct);

        var dup = await svc.CreateAsync(Model("RTR-HUB-01", "192.0.2.2"), Ct);

        Assert.False(dup.Ok);
        Assert.Equal(nameof(NodeEditModel.Hostname), dup.Field);
    }

    [Fact]
    public async Task Updating_tags_reuses_existing_tags_and_prunes_orphans()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = new NodeService(test.Factory, test.Clock);
        var a = (await svc.CreateAsync(Model("rtr-a", tags: "core; lab"), Ct)).Id!.Value;
        await svc.CreateAsync(Model("rtr-b", "192.0.2.2", tags: "CORE"), Ct);

        Assert.Equal(["core", "lab"], await svc.AllTagsAsync(Ct));

        await svc.UpdateAsync(a, Model("rtr-a", tags: "core"), Ct);
        Assert.Equal(["core"], await svc.AllTagsAsync(Ct));
    }

    [Fact]
    public async Task Delete_removes_node_and_unused_tags()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = new NodeService(test.Factory, test.Clock);
        var id = (await svc.CreateAsync(Model(tags: "lab"), Ct)).Id!.Value;

        Assert.True(await svc.DeleteAsync(id, Ct));
        Assert.Empty(await svc.ListAsync(Ct));
        Assert.Empty(await svc.AllTagsAsync(Ct));
        Assert.False(await svc.DeleteAsync(id, Ct));
    }

    [Fact]
    public async Task List_state_is_worst_enabled_source_and_Unknown_without_sources()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = new NodeService(test.Factory, test.Clock);
        var withSources = (await svc.CreateAsync(Model("rtr-a"), Ct)).Id!.Value;
        await svc.CreateAsync(Model("rtr-b", "192.0.2.2"), Ct);

        await using (var db = test.Open())
        {
            db.StatusSources.AddRange(
                new StatusSource { NodeId = withSources, SourceType = StatusSourceType.Icmp, State = Severity.Normal },
                new StatusSource { NodeId = withSources, SourceType = StatusSourceType.Cpu, State = Severity.Warning },
                new StatusSource { NodeId = withSources, SourceType = StatusSourceType.Memory, State = Severity.Critical, Enabled = false });
            await db.SaveChangesAsync(Ct);
        }

        var list = await svc.ListAsync(Ct);
        Assert.Equal(Severity.Warning, list.Single(n => n.Hostname == "rtr-a").State);
        Assert.Equal(Severity.Unknown, list.Single(n => n.Hostname == "rtr-b").State);
    }

    [Fact]
    public async Task Import_preview_classifies_create_update_unchanged_and_changes_nothing()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = new NodeService(test.Factory, test.Clock);
        await svc.CreateAsync(Model("rtr-same", "192.0.2.1"), Ct);
        await svc.CreateAsync(Model("rtr-changed", "192.0.2.2"), Ct);

        var csv = "hostname,management_ip,site,icon,snmp_enabled\r\n"
                  + "rtr-same,192.0.2.1,HQ,router,true\r\n"
                  + "rtr-changed,192.0.2.22,HQ,router,true\r\n"
                  + "rtr-new,192.0.2.3,Branch,router,false\r\n";
        var preview = await svc.PreviewImportAsync(NodeCsv.Parse(csv), Ct);

        Assert.True(preview.CanApply);
        Assert.Equal(ImportAction.Unchanged, preview.Rows[0].Action);
        Assert.Equal(ImportAction.Update, preview.Rows[1].Action);
        Assert.Contains(preview.Rows[1].Messages, m => m.Contains("192.0.2.22"));
        Assert.Equal(ImportAction.Create, preview.Rows[2].Action);
        Assert.Equal(2, (await svc.ListAsync(Ct)).Count);
    }

    [Fact]
    public async Task Import_apply_creates_and_updates_and_is_audited()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = new NodeService(test.Factory, test.Clock);
        await svc.CreateAsync(Model("rtr-changed", "192.0.2.2"), Ct);

        var csv = "hostname,management_ip,tags\r\nRTR-CHANGED,192.0.2.22,core\r\nrtr-new,192.0.2.3,core;lab\r\n";
        var result = await svc.ApplyImportAsync(NodeCsv.Parse(csv), Ct);

        Assert.Equal(new ImportResult(1, 1, 0), result);
        var list = await svc.ListAsync(Ct);
        Assert.Equal("192.0.2.22", list.Single(n => n.Hostname == "rtr-changed").ManagementIp);
        Assert.Equal(["core", "lab"], list.Single(n => n.Hostname == "rtr-new").Tags);

        await using var db = test.Open();
        Assert.Equal(2, await db.AuditEntries.CountAsync(a => a.EntityType == nameof(Node) && a.Action == "Create", Ct));
        Assert.Equal(1, await db.AuditEntries.CountAsync(a => a.EntityType == nameof(Node) && a.Action == "Update", Ct));
    }

    [Fact]
    public async Task Import_with_any_error_applies_nothing()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = new NodeService(test.Factory, test.Clock);
        var parsed = NodeCsv.Parse("hostname,management_ip\r\nrtr-good,192.0.2.1\r\nrtr-bad,not-an-ip\r\n");

        var preview = await svc.PreviewImportAsync(parsed, Ct);
        Assert.False(preview.CanApply);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ApplyImportAsync(parsed, Ct));
        Assert.Empty(await svc.ListAsync(Ct));
    }

    [Fact]
    public async Task Export_reimports_as_unchanged()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = new NodeService(test.Factory, test.Clock);
        await svc.CreateAsync(Model("rtr-a", tags: "core; lab"), Ct);
        await svc.CreateAsync(Model("sw-b", "2001:db8::b"), Ct);

        var preview = await svc.PreviewImportAsync(NodeCsv.Parse(await svc.ExportCsvAsync(Ct)), Ct);

        Assert.All(preview.Rows, r => Assert.Equal(ImportAction.Unchanged, r.Action));
    }

    private static async Task<(int Snmp, int Ssh)> ProfilesAsync(TestDatabase test)
    {
        var creds = new CredentialService(test.Factory, test.Protector, test.Clock);
        var snmp = await creds.CreateAsync(new CredentialEditModel
        {
            Name = "lab-v3", Kind = CredentialKind.SnmpV3, Username = "yanmt-ro", Port = 161,
            AuthPassword = "lab-auth-pass-01", PrivPassword = "lab-priv-pass-01",
        }, Ct);
        var ssh = await creds.CreateAsync(new CredentialEditModel
        {
            Name = "lab-ssh", Kind = CredentialKind.Ssh, Username = "yanmt", Port = 22, Password = "lab-ssh-pass-01",
        }, Ct);
        return (snmp.Id!.Value, ssh.Id!.Value);
    }

    [Fact]
    public async Task Profiles_are_assigned_and_checked_by_kind()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (snmp, ssh) = await ProfilesAsync(test);
        var svc = new NodeService(test.Factory, test.Clock);

        var model = Model();
        model.SnmpProfileId = snmp;
        model.SshProfileId = ssh;
        var created = await svc.CreateAsync(model, Ct);
        Assert.True(created.Ok, created.Error);
        var edit = (await svc.GetForEditAsync(created.Id!.Value, Ct))!;
        Assert.Equal((snmp, ssh), (edit.SnmpProfileId!.Value, edit.SshProfileId!.Value));

        var swapped = Model("rtr-b", "192.0.2.2");
        swapped.SnmpProfileId = ssh; // an SSH profile in the SNMP slot
        var wrong = await svc.CreateAsync(swapped, Ct);
        Assert.False(wrong.Ok);
        Assert.Equal(nameof(NodeEditModel.SnmpProfileId), wrong.Field);
    }

    [Fact]
    public async Task Csv_import_resolves_profiles_by_name_and_export_round_trips()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (snmp, ssh) = await ProfilesAsync(test);
        var svc = new NodeService(test.Factory, test.Clock);

        var parsed = NodeCsv.Parse("hostname,management_ip,snmp_profile,ssh_profile\r\nrtr-a,192.0.2.1,LAB-V3,lab-ssh\r\n");
        await svc.ApplyImportAsync(parsed, Ct);
        var node = (await svc.ListAsync(Ct)).Single();
        var edit = (await svc.GetForEditAsync(node.Id, Ct))!;
        Assert.Equal((snmp, ssh), (edit.SnmpProfileId!.Value, edit.SshProfileId!.Value));

        var csv = await svc.ExportCsvAsync(Ct);
        Assert.Contains("lab-v3,lab-ssh", csv);
        var preview = await svc.PreviewImportAsync(NodeCsv.Parse(csv), Ct);
        Assert.All(preview.Rows, r => Assert.Equal(ImportAction.Unchanged, r.Action));

        // Clearing a profile cell unassigns it, and the preview says so.
        var cleared = await svc.PreviewImportAsync(NodeCsv.Parse("hostname,management_ip,snmp_profile,ssh_profile\r\nrtr-a,192.0.2.1,lab-v3,\r\n"), Ct);
        Assert.Equal(ImportAction.Update, cleared.Rows[0].Action);
        Assert.Contains(cleared.Rows[0].Messages, m => m.StartsWith("SSH profile"));
    }

    [Fact]
    public async Task Csv_import_rejects_unknown_or_wrong_kind_profiles()
    {
        await using var test = await TestDatabase.CreateAsync();
        await ProfilesAsync(test);
        var svc = new NodeService(test.Factory, test.Clock);

        var preview = await svc.PreviewImportAsync(NodeCsv.Parse(
            "hostname,management_ip,snmp_profile,ssh_profile\r\nrtr-a,192.0.2.1,nope,\r\nrtr-b,192.0.2.2,lab-ssh,\r\n"), Ct);

        Assert.False(preview.CanApply);
        Assert.Contains("no credential profile named 'nope'", preview.Rows[0].Messages[0]);
        Assert.Contains("is an Ssh profile", preview.Rows[1].Messages[0]);
    }
}
