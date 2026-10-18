using Lextm.SharpSnmpLib;
using Microsoft.Extensions.Logging.Abstractions;
using Yanmt.Core.Connectivity;
using Yanmt.Core.Credentials;
using Yanmt.Infrastructure.Collectors.Icmp;
using Yanmt.Infrastructure.Collectors.Snmp;
using Yanmt.Infrastructure.Collectors.Ssh;
using Yanmt.Infrastructure.Connectivity;
using Yanmt.Infrastructure.Credentials;
using Yanmt.Infrastructure.Inventory;
using Yanmt.Infrastructure.Tests.Collectors;

namespace Yanmt.Infrastructure.Tests.Connectivity;

public class ConnectionTesterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ConnectionTester Tester(TestDatabase test) => new(
        test.Factory, new CredentialService(test.Factory, test.Protector, test.Clock), new IcmpProbe(), new SnmpV3Reader(),
        new ReadOnlySshCollector(), NullLogger<ConnectionTester>.Instance);

    private static async Task<int> NodeAsync(TestDatabase test, int? snmpProfile = null, int? sshProfile = null)
    {
        var result = await new NodeService(test.Factory, test.Clock).CreateAsync(new NodeEditModel
        {
            Hostname = "lab-rtr-hub-01",
            ManagementIp = "127.0.0.1",
            SnmpProfileId = snmpProfile,
            SshProfileId = sshProfile,
        }, Ct);
        Assert.True(result.Ok, result.Error);
        return result.Id!.Value;
    }

    private static async Task<int> SnmpProfileAsync(TestDatabase test, int port, SnmpPrivProtocol priv = SnmpPrivProtocol.Aes128)
    {
        var result = await new CredentialService(test.Factory, test.Protector, test.Clock).CreateAsync(new CredentialEditModel
        {
            Name = "lab-v3",
            Kind = CredentialKind.SnmpV3,
            Username = "yanmt-ro",
            Port = port,
            SnmpAuth = SnmpAuthProtocol.Sha256,
            SnmpPriv = priv,
            AuthPassword = "lab-auth-pass-01",
            PrivPassword = "lab-priv-pass-01",
        }, Ct);
        Assert.True(result.Ok, result.Error);
        return result.Id!.Value;
    }

    [Fact]
    public async Task Icmp_to_loopback_succeeds()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await NodeAsync(test);

        var result = await Tester(test).TestIcmpAsync(node, Ct);

        Assert.True(result.Success, result.Summary);
        Assert.Contains(result.Details, d => d.Key == "Replies" && d.Value.StartsWith("4 of 4"));
    }

    [Theory]
    [InlineData(SnmpPrivProtocol.Aes128)]
    [InlineData(SnmpPrivProtocol.Aes256)]
    public async Task Snmp_test_reads_system_info_through_the_assigned_profile(SnmpPrivProtocol priv)
    {
        await using var test = await TestDatabase.CreateAsync();
        using var agent = new FakeSnmpAgent(
            new SnmpV3Credential("yanmt-ro", SnmpAuthProtocol.Sha256, "lab-auth-pass-01", priv, "lab-priv-pass-01"),
            new Dictionary<string, ISnmpData>
            {
                ["1.3.6.1.2.1.1.1.0"] = new OctetString("Cisco IOS XE Software, Version 17.12.04"),
                ["1.3.6.1.2.1.1.5.0"] = new OctetString("lab-rtr-hub-01"),
                ["1.3.6.1.2.1.1.3.0"] = new TimeTicks(123456),
            });
        var node = await NodeAsync(test, snmpProfile: await SnmpProfileAsync(test, agent.EndPoint.Port, priv));

        var result = await Tester(test).TestSnmpAsync(node, Ct);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("Cisco IOS XE Software, Version 17.12.04", result.Output);
        Assert.Contains(result.Details, d => d.Key == "sysName" && d.Value == "lab-rtr-hub-01");
        Assert.Contains(CredentialRules.Describe(priv), result.Summary);
    }

    [Fact]
    public async Task Snmp_view_without_the_system_group_is_explained()
    {
        await using var test = await TestDatabase.CreateAsync();
        using var agent = new FakeSnmpAgent(
            new SnmpV3Credential("yanmt-ro", SnmpAuthProtocol.Sha256, "lab-auth-pass-01", SnmpPrivProtocol.Aes128, "lab-priv-pass-01"),
            new Dictionary<string, ISnmpData>());
        var node = await NodeAsync(test, snmpProfile: await SnmpProfileAsync(test, agent.EndPoint.Port));

        var result = await Tester(test).TestSnmpAsync(node, Ct);

        Assert.False(result.Success);
        Assert.Equal(FailureCategory.DeviceError, result.Failure);
        Assert.Contains("view", result.Summary);
    }

    [Fact]
    public async Task Missing_profiles_are_reported_as_not_configured()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await NodeAsync(test);
        var tester = Tester(test);

        Assert.Equal(FailureCategory.NotConfigured, (await tester.TestSnmpAsync(node, Ct)).Failure);
        Assert.Equal(FailureCategory.NotConfigured, (await tester.TestSshAsync(node, cancellationToken: Ct)).Failure);
        Assert.Equal(FailureCategory.NotConfigured, (await tester.TestIcmpAsync(9999, Ct)).Failure);
    }
}
