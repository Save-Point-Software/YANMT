using System.Net;
using Yanmt.Core.Connectivity;
using Yanmt.Core.Credentials;
using Yanmt.Infrastructure.Collectors.Snmp;
using Yanmt.Infrastructure.Credentials;

namespace Yanmt.Integration.Tests;

/// <summary>
/// SNMPv3 authPriv against real IOS-XE. Capability-aware: each profile runs only against the nodes
/// whose snmpProfiles list it in lab.json, and skips when none do. Each read also checks the
/// response HMAC (UsmAuthentication), so a pass proves our HMAC verification and key localization
/// match Cisco's, not just SharpSnmpLib's.
/// </summary>
[Trait("Category", "Integration")]
[TestCaseOrderer(typeof(AlphabeticalOrderer))]
public class SnmpLabTests
{
    private const string SysDescr = "1.3.6.1.2.1.1.1.0";
    private const string SysName = "1.3.6.1.2.1.1.5.0";
    private const string SysUpTime = "1.3.6.1.2.1.1.3.0";

    private const string Aes256NotSupported =
        "SHA-256/AES-256 is not supported in v1: in the CML lab IOS-XE (Hub-R2) answered usmStatsWrongDigests " +
        "even with identical, re-entered passwords, while SHA-256/AES-128 passed. See docs/SPEC.md. " +
        "Remove this Skip to re-run the experiment.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, (SnmpAuthProtocol Auth, SnmpPrivProtocol Priv)> Pairings = new(StringComparer.OrdinalIgnoreCase)
    {
        [LabSnmp.Sha256Aes128Key] = (SnmpAuthProtocol.Sha256, SnmpPrivProtocol.Aes128),
        [LabSnmp.Sha1Aes128Key] = (SnmpAuthProtocol.Sha1, SnmpPrivProtocol.Aes128),
        [LabSnmp.Sha256Aes256Key] = (SnmpAuthProtocol.Sha256, SnmpPrivProtocol.Aes256),
    };

    [Fact]
    public Task Snmp_1_sha256_aes128_reads_system_group_on_its_nodes() => ReadSystemGroupAsync(LabSnmp.Sha256Aes128Key);

    [Fact]
    public Task Snmp_2_sha1_aes128_legacy_reads_system_group_on_its_nodes() => ReadSystemGroupAsync(LabSnmp.Sha1Aes128Key);

    [Fact(Skip = Aes256NotSupported)]
    public Task Snmp_3_sha256_aes256_reads_system_group_on_its_nodes() => ReadSystemGroupAsync(LabSnmp.Sha256Aes256Key);

    [Theory]
    [InlineData(LabSnmp.Sha256Aes128Key)]
    [InlineData(LabSnmp.Sha1Aes128Key)]
    public async Task Snmp_4_wrong_auth_password_is_reported_as_auth_failure(string profile)
    {
        var (lab, node, cred) = RequireFirstNode(profile);
        var wrong = new SnmpV3Credential(cred.Username, cred.Auth, "wrong-auth-password", cred.Priv, cred.PrivPassword);

        var ex = await Assert.ThrowsAsync<SnmpQueryException>(() => new SnmpV3Reader().GetAsync(Target(lab, node, wrong), [SysDescr], Ct));

        TestContext.Current.TestOutputHelper?.WriteLine($"{node.Hostname}: {ex.Category}: {ex.Message}");
        Assert.Equal(FailureCategory.AuthFailure, ex.Category);
    }

    [Theory]
    [InlineData(LabSnmp.Sha256Aes128Key)]
    [InlineData(LabSnmp.Sha1Aes128Key)]
    public async Task Snmp_5_wrong_priv_password_is_reported_and_returns_no_data(string profile)
    {
        var (lab, node, cred) = RequireFirstNode(profile);
        var wrong = new SnmpV3Credential(cred.Username, cred.Auth, cred.AuthPassword, cred.Priv, "wrong-priv-password");

        var ex = await Assert.ThrowsAsync<SnmpQueryException>(() => new SnmpV3Reader().GetAsync(Target(lab, node, wrong), [SysDescr], Ct));

        // In the lab both Hub-R2 and ISP-HUB drop it silently (Timeout after a successful discovery);
        // a usmStatsDecryptionErrors report is also acceptable. See docs/SPEC.md, SNMPv3 lab results.
        TestContext.Current.TestOutputHelper?.WriteLine($"{node.Hostname}: {ex.Category}: {ex.Message}");
        Assert.Contains(ex.Category, new[] { FailureCategory.DecryptionError, FailureCategory.Timeout });
        if (ex.Category == FailureCategory.Timeout)
        {
            Assert.Contains("privacy password", ex.Message);
        }
    }

    [Theory]
    [InlineData(LabSnmp.Sha256Aes128Key)]
    [InlineData(LabSnmp.Sha1Aes128Key)]
    public async Task Snmp_6_unknown_user_is_reported(string profile)
    {
        var (lab, node, cred) = RequireFirstNode(profile);
        var unknown = new SnmpV3Credential("yanmt-no-such-user", cred.Auth, cred.AuthPassword, cred.Priv, cred.PrivPassword);

        var ex = await Assert.ThrowsAsync<SnmpQueryException>(() => new SnmpV3Reader().GetAsync(Target(lab, node, unknown), [SysDescr], Ct));

        TestContext.Current.TestOutputHelper?.WriteLine($"{node.Hostname}: {ex.Category}: {ex.Message}");
        Assert.Equal(FailureCategory.UnknownUser, ex.Category);
    }

    private static async Task ReadSystemGroupAsync(string profile)
    {
        var (lab, nodes, cred) = Require(profile);
        var failures = new List<string>();
        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"{profile}: testing {string.Join(", ", nodes.Select(n => n.Hostname))}; " +
                          $"not listed on {string.Join(", ", lab.Nodes.Except(nodes).Select(n => n.Hostname).DefaultIfEmpty("(none)"))}");

        foreach (var node in nodes)
        {
            try
            {
                var result = await new SnmpV3Reader().GetAsync(Target(lab, node, cred), [SysDescr, SysName, SysUpTime], Ct);
                Assert.All(result.Values, v => Assert.False(v.IsMissing, $"{node.Hostname}: {v.Oid} missing (check the SNMP view)"));
                output?.WriteLine($"{node.Hostname}: engine {result.Engine.EngineId}, sysName={result.Values[1].Text}, uptime={result.Values[2].Text}");
            }
            catch (SnmpQueryException ex)
            {
                failures.Add($"{node.Hostname} ({node.ManagementIp}): {ex.Category}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{CredentialRules.Describe(cred.Auth)}/{CredentialRules.Describe(cred.Priv)} failed on:\n" + string.Join("\n", failures));
    }

    /// <summary>The profile's credential and the nodes that list it, or skips the test.</summary>
    private static (LabConfig Lab, IReadOnlyList<LabNode> Nodes, SnmpV3Credential Credential) Require(string profile)
    {
        var lab = LabConfig.Require();
        var user = lab.Snmp?.Profile(profile);
        Assert.SkipWhen(user is null, $"snmp.{profile} isn't defined in lab.json.");
        var nodes = lab.NodesFor(profile);
        Assert.SkipWhen(nodes.Count == 0, $"No node in lab.json lists \"{profile}\" in snmpProfiles.");

        var (auth, priv) = Pairings[profile];
        return (lab, nodes, new SnmpV3Credential(user!.User, auth, user.AuthPassword, priv, user.PrivPassword));
    }

    private static (LabConfig Lab, LabNode Node, SnmpV3Credential Credential) RequireFirstNode(string profile)
    {
        var (lab, nodes, cred) = Require(profile);
        return (lab, nodes[0], cred);
    }

    private static SnmpTarget Target(LabConfig lab, LabNode node, SnmpV3Credential cred) =>
        new(IPAddress.Parse(node.ManagementIp), lab.Snmp?.Port ?? 161, cred, TimeSpan.FromSeconds(5));
}
