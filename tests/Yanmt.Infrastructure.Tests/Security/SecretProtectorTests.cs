using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Yanmt.Infrastructure.Security;

namespace Yanmt.Infrastructure.Tests.Security;

public sealed class SecretProtectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yanmt-tests", Guid.NewGuid().ToString("N"));

    public SecretProtectorTests() => Directory.CreateDirectory(_dir);

    private SecretProtector Create(string? dir = null) =>
        new(new YanmtPaths(dir ?? _dir), NullLogger<SecretProtector>.Instance);

    [Fact]
    public void Round_trips_and_does_not_contain_the_plaintext()
    {
        var protector = Create();
        var secret = Encoding.UTF8.GetBytes("{\"Password\":\"lab-ssh-pass-01\"}");

        var blob = protector.Protect(secret);

        Assert.Equal(1, blob[0]); // format version
        Assert.Equal(-1, blob.AsSpan().IndexOf(Encoding.UTF8.GetBytes("lab-ssh-pass-01")));
        Assert.Equal(secret, protector.Unprotect(blob));
    }

    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier NetworkService = new(WellKnownSidType.NetworkServiceSid, null);

    private static HashSet<SecurityIdentifier> AllowedSids(FileSystemSecurity acl) =>
        acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .Select(r => (SecurityIdentifier)r.IdentityReference)
            .ToHashSet();

    [Fact]
    public void Creates_the_entropy_file_once_with_a_protected_acl()
    {
        var protector = Create();
        protector.EnsureReady();
        Assert.True(protector.CreatedNewKey);

        var path = new YanmtPaths(_dir).EntropyFile;
        Assert.Equal(32, new FileInfo(path).Length);

        var acl = new FileInfo(path).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected, "Entropy file must not inherit the data folder's ACL.");
        var sids = AllowedSids(acl);
        Assert.Contains(WindowsIdentity.GetCurrent().User!, sids);
        Assert.Contains(LocalSystem, sids);
        Assert.Contains(Admins, sids);
        Assert.DoesNotContain(sids, s => Core.Security.EntropyFileRules.IsBroadPrincipal(s.Value));

        var second = Create();
        second.EnsureReady();
        Assert.False(second.CreatedNewKey);
    }

    [Fact]
    public void Entropy_file_gets_the_accounts_the_data_folder_grants_so_any_creator_leaves_it_readable()
    {
        // The data folder grants the "service account" (NETWORK SERVICE stands in) Modify for files.
        var folder = new DirectoryInfo(_dir);
        var folderAcl = folder.GetAccessControl();
        folderAcl.AddAccessRule(new FileSystemAccessRule(NetworkService, FileSystemRights.Modify,
            InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        folder.SetAccessControl(folderAcl);

        Create().EnsureReady();

        var rules = new FileInfo(new YanmtPaths(_dir).EntropyFile).GetAccessControl()
            .GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        var service = Assert.Single(rules, r => r.IdentityReference.Equals(NetworkService));
        Assert.True(service.FileSystemRights.HasFlag(FileSystemRights.Read), $"service account rights: {service.FileSystemRights}");
    }

    [Fact]
    public void Acl_copies_file_inheritable_entries_but_never_broad_groups()
    {
        var self = WindowsIdentity.GetCurrent().User!;
        var folder = new DirectorySecurity();
        folder.SetAccessRuleProtection(true, false);
        var both = InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit;
        folder.AddAccessRule(new FileSystemAccessRule(NetworkService, FileSystemRights.Modify, both, PropagationFlags.None, AccessControlType.Allow));
        folder.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, both, PropagationFlags.None, AccessControlType.Allow));
        folder.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null),
            FileSystemRights.Read, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow)); // folders only
        folder.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null),
            FileSystemRights.FullControl, both, PropagationFlags.InheritOnly, AccessControlType.Allow));

        var file = SecretProtector.BuildEntropyFileSecurity(folder, self);

        Assert.True(file.AreAccessRulesProtected);
        var sids = AllowedSids(file);
        Assert.True(sids.SetEquals([NetworkService, self, LocalSystem, Admins]), "ACL: " + string.Join(", ", sids));
        Assert.All(file.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            r => Assert.Equal(InheritanceFlags.None, r.InheritanceFlags));
    }

    [Fact]
    public void Unreadable_existing_file_names_the_account_and_the_fix()
    {
        Create().EnsureReady();
        var path = new YanmtPaths(_dir).EntropyFile;
        var self = WindowsIdentity.GetCurrent().User!;
        var file = new FileInfo(path);
        var original = file.GetAccessControl();
        var denied = file.GetAccessControl();
        denied.AddAccessRule(new FileSystemAccessRule(self, FileSystemRights.ReadData, AccessControlType.Deny));
        file.SetAccessControl(denied);
        try
        {
            var ex = Assert.Throws<EntropyFileUnreadableException>(() => Create().EnsureReady());

            Assert.Equal(path, ex.Path);
            Assert.Contains($"icacls \"{path}\" /grant \"{ex.Account}:R\"", ex.Message);
            Assert.Equal(32, new FileInfo(path).Length); // never replaced
        }
        finally
        {
            file.SetAccessControl(original);
        }
    }

    [Fact]
    public void Blob_from_another_entropy_file_is_unreadable()
    {
        var blob = Create().Protect("x"u8);

        var otherDir = Path.Combine(_dir, "other");
        Directory.CreateDirectory(otherDir);
        var ex = Assert.Throws<SecretUnreadableException>(() => Create(otherDir).Unprotect(blob));
        Assert.Contains("re-enter", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tampered_or_unknown_format_blob_is_unreadable()
    {
        var protector = Create();
        var blob = protector.Protect("x"u8);

        var tampered = (byte[])blob.Clone();
        tampered[^5] ^= 0xFF;
        Assert.Throws<SecretUnreadableException>(() => protector.Unprotect(tampered));

        var wrongVersion = (byte[])blob.Clone();
        wrongVersion[0] = 2;
        Assert.Throws<SecretUnreadableException>(() => protector.Unprotect(wrongVersion));

        Assert.Throws<SecretUnreadableException>(() => protector.Unprotect([]));
    }

    [Fact]
    public void Wrong_size_entropy_file_stops_startup_instead_of_being_replaced()
    {
        var path = new YanmtPaths(_dir).EntropyFile;
        File.WriteAllBytes(path, new byte[5]);

        var ex = Assert.Throws<InvalidOperationException>(() => Create().EnsureReady());

        Assert.Contains("expected 32", ex.Message);
        Assert.Equal(5, new FileInfo(path).Length);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }
}
