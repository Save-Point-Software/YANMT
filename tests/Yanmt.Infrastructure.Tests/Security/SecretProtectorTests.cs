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
        var sids = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(r => (SecurityIdentifier)r.IdentityReference)
            .ToHashSet();
        var expected = new HashSet<SecurityIdentifier>
        {
            WindowsIdentity.GetCurrent().User!,
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
        };
        Assert.True(sids.SetEquals(expected), "ACL: " + string.Join(", ", sids));

        var second = Create();
        second.EnsureReady();
        Assert.False(second.CreatedNewKey);
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
