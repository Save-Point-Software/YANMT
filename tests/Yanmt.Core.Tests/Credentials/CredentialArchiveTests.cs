using System.Text;
using Yanmt.Core.Credentials;

namespace Yanmt.Core.Tests.Credentials;

public class CredentialArchiveTests
{
    private const string Pass = "correct horse battery";
    private const int Fast = PassphraseCrypto.MinIterations;

    private static readonly ArchivedProfile Snmp = new("lab-v3", CredentialKind.SnmpV3, "yanmt-ro", 161, SnmpAuthProtocol.Sha256, SnmpPrivProtocol.Aes128, null, "lab-auth-pass-01", "lab-priv-pass-01");
    private static readonly ArchivedProfile Ssh = new("lab-ssh", CredentialKind.Ssh, "yanmt", 22, null, null, "lab-ssh-pass-01", null, null);

    [Fact]
    public void Round_trips_profiles_and_secrets()
    {
        var file = CredentialArchive.Seal([Snmp, Ssh], Pass, Fast);

        var opened = CredentialArchive.Open(file, Pass);

        Assert.Equal([Snmp, Ssh], opened);
    }

    [Fact]
    public void File_does_not_contain_plaintext()
    {
        var file = CredentialArchive.Seal([Snmp, Ssh], Pass, Fast);
        var text = Encoding.UTF8.GetString(file);

        Assert.DoesNotContain("lab-auth-pass-01", text);
        Assert.DoesNotContain("lab-ssh-pass-01", text);
        Assert.DoesNotContain("yanmt-ro", text);
    }

    [Fact]
    public void Wrong_passphrase_returns_null()
    {
        var file = CredentialArchive.Seal([Snmp], Pass, Fast);

        Assert.Null(CredentialArchive.Open(file, "another passphrase!"));
    }

    [Fact]
    public void Every_modified_byte_is_rejected()
    {
        var file = CredentialArchive.Seal([Snmp], Pass, Fast);

        for (var i = 0; i < file.Length; i++)
        {
            var tampered = (byte[])file.Clone();
            tampered[i] ^= 0x01;
            // Magic or iteration bytes make it "not an export"; anything else fails authentication.
            try
            {
                Assert.Null(CredentialArchive.Open(tampered, Pass));
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    [Fact]
    public void Truncated_or_appended_files_are_rejected()
    {
        var file = CredentialArchive.Seal([Snmp], Pass, Fast);

        Assert.Null(CredentialArchive.Open(file.Append((byte)0).ToArray(), Pass));
        Assert.Null(CredentialArchive.Open(file[..^1], Pass));
        Assert.Throws<InvalidDataException>(() => CredentialArchive.Open(file[..10], Pass));
    }

    [Fact]
    public void Two_exports_of_the_same_data_differ()
    {
        Assert.NotEqual(CredentialArchive.Seal([Snmp], Pass, Fast), CredentialArchive.Seal([Snmp], Pass, Fast));
    }

    [Fact]
    public void Not_an_export_is_reported()
    {
        Assert.Throws<InvalidDataException>(() => CredentialArchive.Open(Encoding.UTF8.GetBytes("hello, this is a text file, not an export"), Pass));
    }

    [Fact]
    public void Absurd_iteration_counts_are_refused_before_deriving_a_key()
    {
        var file = CredentialArchive.Seal([Snmp], Pass, Fast);
        file[8] = 0x7F; // iterations = 0x7F0186A0, far above the cap

        Assert.Throws<InvalidDataException>(() => CredentialArchive.Open(file, Pass));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("elevenchars")]
    public void Short_passphrases_are_refused(string passphrase)
    {
        Assert.NotNull(PassphraseCrypto.ValidatePassphrase(passphrase));
        Assert.Throws<ArgumentException>(() => CredentialArchive.Seal([Snmp], passphrase, Fast));
    }

    [Fact]
    public void Profile_validation_applies_the_form_rules()
    {
        Assert.Null(Snmp.Validate());
        Assert.Null(Ssh.Validate());
        Assert.NotNull((Snmp with { Port = 0 }).Validate());
        Assert.NotNull((Snmp with { AuthPassword = "short" }).Validate());
        Assert.NotNull((Snmp with { PrivPassword = null }).Validate());
        Assert.NotNull((Snmp with { SnmpAuth = null }).Validate());
        Assert.NotNull((Snmp with { SnmpAuth = SnmpAuthProtocol.Sha1, SnmpPriv = SnmpPrivProtocol.Aes256 }).Validate());
        Assert.NotNull((Ssh with { Password = null }).Validate());
        Assert.NotNull((Ssh with { Kind = (CredentialKind)99 }).Validate());
    }

    [Fact]
    public void ToString_hides_secrets()
    {
        Assert.DoesNotContain("lab-auth-pass-01", Snmp.ToString());
    }
}
