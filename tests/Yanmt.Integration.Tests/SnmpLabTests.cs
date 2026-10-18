using System.Net;
using Yanmt.Core.Connectivity;
using Yanmt.Core.Credentials;
using Yanmt.Infrastructure.Collectors.Snmp;
using Yanmt.Infrastructure.Credentials;

namespace Yanmt.Integration.Tests;

/// <summary>
/// SNMPv3 authPriv against real IOS-XE: SHA-256/AES-128 first, then SHA-256/AES-256, as the spec
/// asks. Each run also checks the response HMAC (UsmAuthentication), so a pass proves our HMAC
/// verification and key localization match Cisco's, not just SharpSnmpLib's.
/// </summary>
[Trait("Category", "Integration")]
[TestCaseOrderer(typeof(AlphabeticalOrderer))]
public class SnmpLabTests
{
    private const string SysDescr = "1.3.6.1.2.1.1.1.0";
    private const string SysName = "1.3.6.1.2.1.1.5.0";
    private const string SysUpTime = "1.3.6.1.2.1.1.3.0";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public Task Snmp_1_sha256_aes128_reads_system_group_on_every_node() =>
        ReadSystemGroupAsync(lab => lab.Snmp?.Sha256Aes128, SnmpPrivProtocol.Aes128);

    [Fact]
    public Task Snmp_2_sha256_aes256_reads_system_group_on_every_node() =>
        ReadSystemGroupAsync(lab => lab.Snmp?.Sha256Aes256, SnmpPrivProtocol.Aes256);

    [Fact]
    public async Task Snmp_3_wrong_auth_password_is_reported_as_auth_failure()
    {
        var (lab, user) = Require(l => l.Snmp?.Sha256Aes128);
        var node = lab.Nodes[0];
        var cred = new SnmpV3Credential(user.User, SnmpAuthProtocol.Sha256, "wrong-auth-password", SnmpPrivProtocol.Aes128, user.PrivPassword);

        var ex = await Assert.ThrowsAsync<SnmpQueryException>(() => new SnmpV3Reader().GetAsync(Target(lab, node, cred), [SysDescr], Ct));

        TestContext.Current.TestOutputHelper?.WriteLine($"{node.Hostname}: {ex.Category}: {ex.Message}");
        Assert.Equal(FailureCategory.AuthFailure, ex.Category);
    }

    [Fact]
    public async Task Snmp_4_wrong_priv_password_is_reported_and_returns_no_data()
    {
        var (lab, user) = Require(l => l.Snmp?.Sha256Aes128);
        var node = lab.Nodes[0];
        var cred = new SnmpV3Credential(user.User, SnmpAuthProtocol.Sha256, user.AuthPassword, SnmpPrivProtocol.Aes128, "wrong-priv-password");

        var ex = await Assert.ThrowsAsync<SnmpQueryException>(() => new SnmpV3Reader().GetAsync(Target(lab, node, cred), [SysDescr], Ct));

        // IOS-XE answers with usmStatsDecryptionErrors; record what this image actually does.
        TestContext.Current.TestOutputHelper?.WriteLine($"{node.Hostname}: {ex.Category}: {ex.Message}");
        Assert.Contains(ex.Category, new[] { FailureCategory.DecryptionError, FailureCategory.Timeout });
    }

    [Fact]
    public async Task Snmp_5_unknown_user_is_reported()
    {
        var (lab, user) = Require(l => l.Snmp?.Sha256Aes128);
        var node = lab.Nodes[0];
        var cred = new SnmpV3Credential("yanmt-no-such-user", SnmpAuthProtocol.Sha256, user.AuthPassword, SnmpPrivProtocol.Aes128, user.PrivPassword);

        var ex = await Assert.ThrowsAsync<SnmpQueryException>(() => new SnmpV3Reader().GetAsync(Target(lab, node, cred), [SysDescr], Ct));

        TestContext.Current.TestOutputHelper?.WriteLine($"{node.Hostname}: {ex.Category}: {ex.Message}");
        Assert.Equal(FailureCategory.UnknownUser, ex.Category);
    }

    private static async Task ReadSystemGroupAsync(Func<LabConfig, LabSnmpUser?> pick, SnmpPrivProtocol priv)
    {
        var (lab, user) = Require(pick);
        var cred = new SnmpV3Credential(user.User, SnmpAuthProtocol.Sha256, user.AuthPassword, priv, user.PrivPassword);
        var failures = new List<string>();

        foreach (var node in lab.Nodes)
        {
            try
            {
                var result = await new SnmpV3Reader().GetAsync(Target(lab, node, cred), [SysDescr, SysName, SysUpTime], Ct);
                Assert.All(result.Values, v => Assert.False(v.IsMissing, $"{node.Hostname}: {v.Oid} missing (check the SNMP view)"));
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"{node.Hostname}: engine {result.Engine.EngineId}, sysName={result.Values[1].Text}, uptime={result.Values[2].Text}");
            }
            catch (SnmpQueryException ex)
            {
                failures.Add($"{node.Hostname} ({node.ManagementIp}): {ex.Category}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, $"SHA-256/{CredentialRules.Describe(priv)} failed on:\n" + string.Join("\n", failures));
    }

    private static (LabConfig Lab, LabSnmpUser User) Require(Func<LabConfig, LabSnmpUser?> pick)
    {
        var lab = LabConfig.Require();
        var user = pick(lab);
        Assert.SkipWhen(user is null, "This SNMP user isn't configured in lab.json.");
        Assert.NotEmpty(lab.Nodes);
        return (lab, user!);
    }

    private static SnmpTarget Target(LabConfig lab, LabNode node, SnmpV3Credential cred) =>
        new(IPAddress.Parse(node.ManagementIp), lab.Snmp?.Port ?? 161, cred, TimeSpan.FromSeconds(5));
}
