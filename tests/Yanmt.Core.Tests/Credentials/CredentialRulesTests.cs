using Yanmt.Core.Connectivity;
using Yanmt.Core.Credentials;

namespace Yanmt.Core.Tests.Credentials;

public class CredentialRulesTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("lab-snmpv3", true)]
    public void Name_is_required(string? name, bool valid) =>
        Assert.Equal(valid, CredentialRules.ValidateName(name) is null);

    [Fact]
    public void Name_has_a_length_limit()
    {
        Assert.Null(CredentialRules.ValidateName(new string('a', CredentialRules.MaxNameLength)));
        Assert.NotNull(CredentialRules.ValidateName(new string('a', CredentialRules.MaxNameLength + 1)));
    }

    [Theory]
    [InlineData("yanmt", true)]
    [InlineData("yanmt-ro", true)]
    [InlineData("", false)]
    [InlineData("two words", false)]
    [InlineData("tab\tuser", false)]
    [InlineData("new\nline", false)]
    public void Username_rules(string username, bool valid) =>
        Assert.Equal(valid, CredentialRules.ValidateUsername(username) is null);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(22, true)]
    [InlineData(65535, true)]
    [InlineData(65536, false)]
    public void Port_range(int port, bool valid) => Assert.Equal(valid, CredentialRules.ValidatePort(port) is null);

    [Theory]
    [InlineData("1234567", CredentialKind.SnmpV3, false)] // USM minimum is 8
    [InlineData("12345678", CredentialKind.SnmpV3, true)]
    [InlineData("short", CredentialKind.Ssh, true)]
    [InlineData("has\u0001control", CredentialKind.Ssh, false)]
    public void Secret_rules(string secret, CredentialKind kind, bool valid) =>
        Assert.Equal(valid, CredentialRules.ValidateSecret("Password", secret, kind) is null);

    [Fact]
    public void Secret_has_a_length_limit() =>
        Assert.NotNull(CredentialRules.ValidateSecret("Password", new string('x', CredentialRules.MaxSecretLength + 1), CredentialKind.Ssh));

    [Fact]
    public void Only_sha2_plus_legacy_sha1_auth_and_aes_priv_exist()
    {
        // No MD5, no DES. Names are stored in the DB, so renaming one needs a migration.
        Assert.Equal(["Sha256", "Sha384", "Sha512", "Sha1"], Enum.GetNames<SnmpAuthProtocol>());
        Assert.Equal(["Aes128", "Aes192", "Aes256"], Enum.GetNames<SnmpPrivProtocol>());
    }

    [Theory]
    [InlineData(SnmpAuthProtocol.Sha1, SnmpPrivProtocol.Aes128, true)]
    [InlineData(SnmpAuthProtocol.Sha1, SnmpPrivProtocol.Aes192, false)]
    [InlineData(SnmpAuthProtocol.Sha1, SnmpPrivProtocol.Aes256, false)]
    [InlineData(SnmpAuthProtocol.Sha256, SnmpPrivProtocol.Aes128, true)]
    [InlineData(SnmpAuthProtocol.Sha256, SnmpPrivProtocol.Aes256, true)]
    [InlineData(SnmpAuthProtocol.Sha512, SnmpPrivProtocol.Aes192, true)]
    public void Sha1_pairs_only_with_aes128(SnmpAuthProtocol auth, SnmpPrivProtocol priv, bool valid) =>
        Assert.Equal(valid, CredentialRules.ValidateSnmpProtocols(auth, priv) is null);

    [Fact]
    public void Labels_flag_legacy_auth_and_unverified_privacy()
    {
        Assert.Equal("SHA-1 (legacy)", CredentialRules.Label(SnmpAuthProtocol.Sha1));
        Assert.Equal("SHA-256", CredentialRules.Label(SnmpAuthProtocol.Sha256));
        Assert.Equal("AES-128", CredentialRules.Label(SnmpPrivProtocol.Aes128));
        Assert.Equal("AES-192 (not verified on IOS-XE)", CredentialRules.Label(SnmpPrivProtocol.Aes192));
        Assert.Equal("AES-256 (not verified on IOS-XE)", CredentialRules.Label(SnmpPrivProtocol.Aes256));
    }

    [Fact]
    public void Default_ports()
    {
        Assert.Equal(22, CredentialRules.DefaultPort(CredentialKind.Ssh));
        Assert.Equal(161, CredentialRules.DefaultPort(CredentialKind.SnmpV3));
    }

    [Theory]
    [InlineData(UsmReport.UnknownUserNames, FailureCategory.UnknownUser)]
    [InlineData(UsmReport.WrongDigests, FailureCategory.AuthFailure)]
    [InlineData(UsmReport.DecryptionErrors, FailureCategory.DecryptionError)]
    [InlineData(UsmReport.UnsupportedSecLevels, FailureCategory.UnsupportedSecurityLevel)]
    [InlineData(UsmReport.NotInTimeWindows, FailureCategory.NotInTimeWindow)]
    [InlineData(UsmReport.UnknownEngineIds, FailureCategory.UnknownEngine)]
    [InlineData("." + UsmReport.WrongDigests, FailureCategory.AuthFailure)]
    [InlineData("1.3.6.1.2.1.1.1.0", FailureCategory.DeviceError)]
    public void Usm_report_oids_map_to_categories(string oid, FailureCategory expected) =>
        Assert.Equal(expected, UsmReport.Classify(oid).Category);
}
