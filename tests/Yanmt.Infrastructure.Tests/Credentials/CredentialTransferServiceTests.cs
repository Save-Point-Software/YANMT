using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Yanmt.Core.Credentials;
using Yanmt.Infrastructure.Credentials;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Security;

namespace Yanmt.Infrastructure.Tests.Credentials;

public class CredentialTransferServiceTests
{
    private const string Pass = "correct horse battery";
    private const string AuthPass = "lab-auth-pass-01";
    private const string SshPass = "lab-ssh-pass-01";

    private static readonly ClaimsPrincipal Admin = new(new ClaimsIdentity([new Claim(ClaimTypes.Role, YanmtRoles.Admin)], "test", ClaimTypes.Name, ClaimTypes.Role));
    private static readonly ClaimsPrincipal Viewer = new(new ClaimsIdentity([new Claim(ClaimTypes.Role, YanmtRoles.Viewer)], "test", ClaimTypes.Name, ClaimTypes.Role));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (CredentialService Svc, CredentialTransferService Transfer) Services(TestDatabase test)
    {
        var svc = new CredentialService(test.Factory, test.Protector, test.Clock);
        var audit = new AuditLogService(test.Factory, new SystemUser(), test.Clock);
        return (svc, new CredentialTransferService(test.Factory, svc, audit, test.Clock));
    }

    private static async Task SeedAsync(CredentialService svc)
    {
        Assert.True((await svc.CreateAsync(new CredentialEditModel
        {
            Name = "lab-v3", Kind = CredentialKind.SnmpV3, Username = "yanmt-ro", Port = 161,
            SnmpAuth = SnmpAuthProtocol.Sha256, SnmpPriv = SnmpPrivProtocol.Aes128, AuthPassword = AuthPass, PrivPassword = "lab-priv-pass-01",
        }, Ct)).Ok);
        Assert.True((await svc.CreateAsync(new CredentialEditModel
        {
            Name = "lab-ssh", Kind = CredentialKind.Ssh, Username = "yanmt", Port = 22, Password = SshPass,
        }, Ct)).Ok);
    }

    private static int Iterations => PassphraseCrypto.MinIterations;

    [Fact]
    public async Task Export_then_import_on_another_server_restores_working_credentials()
    {
        await using var source = await TestDatabase.CreateAsync();
        var (srcSvc, srcTransfer) = Services(source);
        await SeedAsync(srcSvc);
        var export = await srcTransfer.ExportAsync(Admin, Pass, Iterations, Ct);
        Assert.Equal(2, export.ProfileCount);

        await using var target = await TestDatabase.CreateAsync();
        var (dstSvc, dstTransfer) = Services(target);
        var result = await dstTransfer.ImportAsync(Admin, export.File, Pass, ImportConflict.Skip, Ct);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, result.Created);
        var snmp = await dstSvc.LoadSnmpAsync((await dstSvc.ListAsync(Ct)).Single(p => p.Name == "lab-v3").Id, Ct);
        Assert.Equal(AuthPass, snmp!.Value.Credential.AuthPassword);
        Assert.Equal(161, snmp.Value.Port);
        var ssh = await dstSvc.LoadSshAsync((await dstSvc.ListAsync(Ct)).Single(p => p.Name == "lab-ssh").Id, Ct);
        Assert.Equal(SshPass, ssh!.Password);
    }

    [Fact]
    public async Task Viewers_cannot_export_or_import()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (svc, transfer) = Services(test);
        await SeedAsync(svc);
        var file = (await transfer.ExportAsync(Admin, Pass, Iterations, Ct)).File;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => transfer.ExportAsync(Viewer, Pass, Iterations, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => transfer.ImportAsync(Viewer, file, Pass, ImportConflict.Skip, Ct));
    }

    [Fact]
    public async Task Wrong_passphrase_and_tampered_files_change_nothing()
    {
        await using var source = await TestDatabase.CreateAsync();
        var (srcSvc, srcTransfer) = Services(source);
        await SeedAsync(srcSvc);
        var file = (await srcTransfer.ExportAsync(Admin, Pass, Iterations, Ct)).File;

        await using var target = await TestDatabase.CreateAsync();
        var (dstSvc, dstTransfer) = Services(target);
        var wrong = await dstTransfer.ImportAsync(Admin, file, "not the passphrase", ImportConflict.Skip, Ct);
        file[^20] ^= 0xFF;
        var tampered = await dstTransfer.ImportAsync(Admin, file, Pass, ImportConflict.Skip, Ct);
        var garbage = await dstTransfer.ImportAsync(Admin, Encoding.UTF8.GetBytes("just some text, definitely not an export"), Pass, ImportConflict.Skip, Ct);

        Assert.All([wrong, tampered, garbage], r => Assert.False(r.Ok));
        Assert.Empty(await dstSvc.ListAsync(Ct));
    }

    [Fact]
    public async Task Existing_names_are_kept_or_overwritten_as_asked()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (svc, transfer) = Services(test);
        await SeedAsync(svc);
        var file = (await transfer.ExportAsync(Admin, Pass, Iterations, Ct)).File;

        var id = (await svc.ListAsync(Ct)).Single(p => p.Name == "lab-ssh").Id;
        var edit = (await svc.GetForEditAsync(id, Ct))!;
        edit.Password = "changed-after-export";
        Assert.True((await svc.UpdateAsync(id, edit, Ct)).Ok);

        var skip = await transfer.ImportAsync(Admin, file, Pass, ImportConflict.Skip, Ct);
        Assert.Equal((0, 0, 2), (skip.Created, skip.Updated, skip.Skipped.Count));
        Assert.Equal("changed-after-export", (await svc.LoadSshAsync(id, Ct))!.Password);

        var overwrite = await transfer.ImportAsync(Admin, file, Pass, ImportConflict.Overwrite, Ct);
        Assert.Equal((0, 2), (overwrite.Created, overwrite.Updated));
        Assert.Equal(SshPass, (await svc.LoadSshAsync(id, Ct))!.Password);
        Assert.Equal(2, (await svc.ListAsync(Ct)).Count);
    }

    [Fact]
    public async Task Invalid_profiles_in_a_valid_file_are_rejected_individually()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (svc, transfer) = Services(test);
        var file = CredentialArchive.Seal([
            new ArchivedProfile("good", CredentialKind.Ssh, "yanmt", 22, null, null, SshPass, null, null),
            new ArchivedProfile("bad-port", CredentialKind.Ssh, "yanmt", 70000, null, null, SshPass, null, null),
            new ArchivedProfile("good", CredentialKind.Ssh, "yanmt", 22, null, null, SshPass, null, null),
        ], Pass, Iterations);

        var result = await transfer.ImportAsync(Admin, file, Pass, ImportConflict.Skip, Ct);

        Assert.True(result.Ok);
        Assert.Equal(1, result.Created);
        Assert.Equal(2, result.Rejected.Count);
        Assert.Equal(["good"], (await svc.ListAsync(Ct)).Select(p => p.Name));
    }

    [Fact]
    public async Task Audit_records_counts_only_never_the_passphrase_or_secrets()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (svc, transfer) = Services(test);
        await SeedAsync(svc);
        var file = (await transfer.ExportAsync(Admin, Pass, Iterations, Ct)).File;
        await transfer.ImportAsync(Admin, file, "wrong passphrase!!", ImportConflict.Skip, Ct);
        await transfer.ImportAsync(Admin, file, Pass, ImportConflict.Overwrite, Ct);

        await using var db = test.Open();
        var entries = await db.AuditEntries.AsNoTracking().ToListAsync(Ct);

        Assert.Contains(entries, e => e.Action == "ExportCredentials");
        Assert.Contains(entries, e => e.Action == "ImportCredentialsFailed");
        Assert.Contains(entries, e => e.Action == "ImportCredentials");
        var everything = string.Join("\n", entries.Select(e => e.Changes));
        foreach (var secret in new[] { Pass, "wrong passphrase!!", AuthPass, SshPass, "lab-priv-pass-01" })
        {
            Assert.DoesNotContain(secret, everything);
        }
    }
}
