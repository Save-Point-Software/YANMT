using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yanmt.Core.Credentials;
using Yanmt.Infrastructure.Credentials;
using Yanmt.Infrastructure.Data.Entities;
using Yanmt.Infrastructure.Inventory;
using Yanmt.Infrastructure.Security;

namespace Yanmt.Infrastructure.Tests.Credentials;

public class CredentialServiceTests
{
    private const string AuthPass = "lab-auth-pass-01";
    private const string PrivPass = "lab-priv-pass-01";
    private const string SshPass = "lab-ssh-pass-01";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CredentialService Service(TestDatabase test, SecretProtector? protector = null) =>
        new(test.Factory, protector ?? test.Protector, test.Clock);

    private static CredentialEditModel Snmp(string name = "lab-v3") => new()
    {
        Name = name,
        Kind = CredentialKind.SnmpV3,
        Username = "yanmt-ro",
        Port = 161,
        SnmpAuth = SnmpAuthProtocol.Sha256,
        SnmpPriv = SnmpPrivProtocol.Aes128,
        AuthPassword = AuthPass,
        PrivPassword = PrivPass,
    };

    private static CredentialEditModel Ssh(string name = "lab-ssh") => new()
    {
        Name = name,
        Kind = CredentialKind.Ssh,
        Username = "yanmt",
        Port = 22,
        Password = SshPass,
    };

    private static async Task<int> CreateAsync(CredentialService svc, CredentialEditModel model)
    {
        var result = await svc.CreateAsync(model, Ct);
        Assert.True(result.Ok, result.Error);
        return result.Id!.Value;
    }

    [Fact]
    public async Task Secrets_are_encrypted_at_rest_and_round_trip()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = Service(test);
        var id = await CreateAsync(svc, Snmp());

        var loaded = await svc.LoadSnmpAsync(id, Ct);
        Assert.NotNull(loaded);
        Assert.Equal(AuthPass, loaded.Value.Credential.AuthPassword);
        Assert.Equal(PrivPass, loaded.Value.Credential.PrivPassword);
        Assert.Equal(SnmpPrivProtocol.Aes128, loaded.Value.Credential.Priv);
        Assert.Equal(161, loaded.Value.Port);

        // Not in the DB file in any common encoding (WAL included).
        await using (var db = test.Open())
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", Ct);
        }

        SqliteConnection.ClearAllPools();
        var files = Directory.GetFiles(Path.GetDirectoryName(test.Path)!, "yanmt-test.db*");
        foreach (var file in files)
        {
            var bytes = await File.ReadAllBytesAsync(file, Ct);
            foreach (var secret in new[] { AuthPass, PrivPass })
            {
                Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)));
                Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(secret)));
            }
        }
    }

    [Fact]
    public async Task Edit_model_never_carries_secrets_back()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = Service(test);
        var model = Snmp();
        var id = await CreateAsync(svc, model);

        Assert.Null(model.AuthPassword); // cleared after save
        var edit = await svc.GetForEditAsync(id, Ct);
        Assert.NotNull(edit);
        Assert.False(edit.IsNew);
        Assert.True(edit.StoredSecretsReadable);
        Assert.Null(edit.Password);
        Assert.Null(edit.AuthPassword);
        Assert.Null(edit.PrivPassword);
    }

    [Fact]
    public async Task Blank_secrets_on_update_keep_the_stored_ones()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = Service(test);
        var id = await CreateAsync(svc, Snmp());

        var edit = (await svc.GetForEditAsync(id, Ct))!;
        edit.Username = "yanmt-ro2";
        edit.PrivPassword = "new-priv-pass-02";
        Assert.True((await svc.UpdateAsync(id, edit, Ct)).Ok);

        var loaded = (await svc.LoadSnmpAsync(id, Ct))!.Value.Credential;
        Assert.Equal("yanmt-ro2", loaded.Username);
        Assert.Equal(AuthPass, loaded.AuthPassword);
        Assert.Equal("new-priv-pass-02", loaded.PrivPassword);
    }

    [Fact]
    public async Task Audit_log_never_contains_secrets()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = Service(test);
        var id = await CreateAsync(svc, Snmp());
        var edit = (await svc.GetForEditAsync(id, Ct))!;
        edit.AuthPassword = "rotated-auth-pass";
        await svc.UpdateAsync(id, edit, Ct);
        var sshId = await CreateAsync(svc, Ssh());
        await svc.DeleteAsync(sshId, Ct);

        await using var db = test.Open();
        var entries = await db.AuditEntries.Where(a => a.EntityType == nameof(CredentialProfile)).ToListAsync(Ct);
        Assert.Equal(["Create", "Update", "Create", "Delete"], entries.Select(e => e.Action));
        foreach (var e in entries)
        {
            Assert.DoesNotContain(AuthPass, e.Changes);
            Assert.DoesNotContain(PrivPass, e.Changes);
            Assert.DoesNotContain(SshPass, e.Changes);
            Assert.DoesNotContain("rotated-auth-pass", e.Changes);
            Assert.Contains("\"ProtectedSecrets\"", e.Changes);
            Assert.Contains("***", e.Changes);
        }
    }

    [Fact]
    public async Task New_profile_requires_secrets_and_snmp_minimum_length()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = Service(test);

        var missing = Snmp();
        missing.PrivPassword = null;
        var r1 = await svc.CreateAsync(missing, Ct);
        Assert.False(r1.Ok);
        Assert.Equal(nameof(CredentialEditModel.PrivPassword), r1.Field);

        var shortPass = Snmp();
        shortPass.AuthPassword = "short";
        var r2 = await svc.CreateAsync(shortPass, Ct);
        Assert.False(r2.Ok);
        Assert.Contains("at least 8", r2.Error);
    }

    [Fact]
    public async Task Names_are_unique_case_insensitively()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = Service(test);
        await CreateAsync(svc, Snmp("Lab-V3"));

        var dup = await svc.CreateAsync(Snmp("lab-v3"), Ct);

        Assert.False(dup.Ok);
        Assert.Equal(nameof(CredentialEditModel.Name), dup.Field);
    }

    [Fact]
    public async Task Kind_cannot_change()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = Service(test);
        var id = await CreateAsync(svc, Snmp());

        var edit = (await svc.GetForEditAsync(id, Ct))!;
        edit.Kind = CredentialKind.Ssh;
        edit.Password = SshPass;
        var result = await svc.UpdateAsync(id, edit, Ct);

        Assert.False(result.Ok);
        Assert.Equal(nameof(CredentialEditModel.Kind), result.Field);
    }

    [Fact]
    public async Task Profile_in_use_cannot_be_deleted()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = Service(test);
        var id = await CreateAsync(svc, Ssh());
        var nodes = new NodeService(test.Factory, test.Clock);
        var node = await nodes.CreateAsync(new NodeEditModel { Hostname = "lab-rtr-01", ManagementIp = "192.0.2.1", SshProfileId = id }, Ct);
        Assert.True(node.Ok, node.Error);

        var result = await svc.DeleteAsync(id, Ct);

        Assert.False(result.Ok);
        Assert.Equal(["lab-rtr-01"], result.UsedBy);
        Assert.Single(await svc.ListAsync(Ct));
        Assert.Equal(1, (await svc.ListAsync(Ct))[0].NodeCount);
    }

    [Fact]
    public async Task Secrets_from_another_server_are_flagged_and_must_be_reentered()
    {
        await using var test = await TestDatabase.CreateAsync();
        var id = await CreateAsync(Service(test), Snmp());

        // Same DB, different entropy file: what a restore to a new server looks like.
        var otherDir = Path.Combine(test.Paths.Root, "other-server");
        Directory.CreateDirectory(otherDir);
        var moved = Service(test, new SecretProtector(new YanmtPaths(otherDir), NullLogger<SecretProtector>.Instance));

        await Assert.ThrowsAsync<SecretUnreadableException>(() => moved.LoadSnmpAsync(id, Ct));
        var edit = (await moved.GetForEditAsync(id, Ct))!;
        Assert.False(edit.StoredSecretsReadable);

        edit.AuthPassword = "reentered-auth-01";
        var partial = await moved.UpdateAsync(id, edit, Ct);
        Assert.False(partial.Ok); // both secrets needed when the stored ones are unreadable
        Assert.Equal(nameof(CredentialEditModel.PrivPassword), partial.Field);

        edit.AuthPassword = "reentered-auth-01";
        edit.PrivPassword = "reentered-priv-01";
        Assert.True((await moved.UpdateAsync(id, edit, Ct)).Ok);
        Assert.Equal("reentered-priv-01", (await moved.LoadSnmpAsync(id, Ct))!.Value.Credential.PrivPassword);
    }

    [Fact]
    public async Task Options_are_filtered_by_kind()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = Service(test);
        await CreateAsync(svc, Snmp());
        await CreateAsync(svc, Ssh());

        Assert.Equal(["lab-v3"], (await svc.OptionsAsync(CredentialKind.SnmpV3, Ct)).Select(o => o.Name));
        Assert.Equal(["lab-ssh"], (await svc.OptionsAsync(CredentialKind.Ssh, Ct)).Select(o => o.Name));
    }

    [Fact]
    public void New_snmp_profiles_default_to_sha256_aes128()
    {
        var model = new CredentialEditModel();

        Assert.Equal(CredentialKind.SnmpV3, model.Kind);
        Assert.Equal(SnmpAuthProtocol.Sha256, model.SnmpAuth);
        Assert.Equal(SnmpPrivProtocol.Aes128, model.SnmpPriv);
    }

    [Fact]
    public async Task Legacy_sha1_profile_saves_with_aes128_and_is_flagged()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = Service(test);
        var model = Snmp("isp-sha1");
        model.Username = "yanmt-sha1";
        model.SnmpAuth = SnmpAuthProtocol.Sha1;
        var id = await CreateAsync(svc, model);

        var loaded = await svc.LoadSnmpAsync(id, Ct);
        Assert.Equal(SnmpAuthProtocol.Sha1, loaded!.Value.Credential.Auth);
        Assert.Equal(SnmpPrivProtocol.Aes128, loaded.Value.Credential.Priv);
        Assert.Equal("isp-sha1 (SHA-1, legacy)", Assert.Single(await svc.OptionsAsync(CredentialKind.SnmpV3, Ct)).Display);
        Assert.Contains("SHA-1 (legacy)/AES-128", Assert.Single(await svc.ListAsync(Ct)).Description);
    }

    [Theory]
    [InlineData(SnmpPrivProtocol.Aes192)]
    [InlineData(SnmpPrivProtocol.Aes256)]
    public async Task Sha1_with_long_aes_is_refused(SnmpPrivProtocol priv)
    {
        await using var test = await TestDatabase.CreateAsync();
        var model = Snmp("isp-sha1");
        model.SnmpAuth = SnmpAuthProtocol.Sha1;
        model.SnmpPriv = priv;

        var result = await Service(test).CreateAsync(model, Ct);

        Assert.False(result.Ok);
        Assert.Equal(nameof(CredentialEditModel.SnmpPriv), result.Field);
        Assert.Contains("AES-128", result.Error);
    }

    [Fact]
    public async Task Unverified_privacy_is_flagged_in_the_picker()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = Service(test);
        var model = Snmp("lab-a256");
        model.SnmpPriv = SnmpPrivProtocol.Aes256;
        await CreateAsync(svc, model);

        Assert.Equal("lab-a256 (AES-256, not verified on IOS-XE)", Assert.Single(await svc.OptionsAsync(CredentialKind.SnmpV3, Ct)).Display);
    }
}
