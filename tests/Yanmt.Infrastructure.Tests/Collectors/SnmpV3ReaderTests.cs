using System.Net;
using System.Net.Sockets;
using Lextm.SharpSnmpLib;
using Yanmt.Core.Connectivity;
using Yanmt.Core.Credentials;
using Yanmt.Infrastructure.Collectors.Snmp;
using Yanmt.Infrastructure.Credentials;

namespace Yanmt.Infrastructure.Tests.Collectors;

public class SnmpV3ReaderTests
{
    private const string User = "yanmt-ro";
    private const string AuthPass = "auth-pass-0001";
    private const string PrivPass = "priv-pass-0001";
    private const string SysDescr = "1.3.6.1.2.1.1.1.0";
    private const string SysName = "1.3.6.1.2.1.1.5.0";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, ISnmpData> Table = new()
    {
        [SysDescr] = new OctetString("Cisco IOS XE Software, Version 17.12.04"),
        [SysName] = new OctetString("lab-rtr-hub-01"),
    };

    private static SnmpV3Credential Cred(SnmpAuthProtocol auth, SnmpPrivProtocol priv, string authPass = AuthPass, string privPass = PrivPass, string user = User) =>
        new(user, auth, authPass, priv, privPass);

    private static FakeSnmpAgent Agent(SnmpV3Credential agentSide) => new(agentSide, Table);

    private static SnmpTarget Target(FakeSnmpAgent agent, SnmpV3Credential cred, double timeoutSeconds = 2) =>
        new(IPAddress.Loopback, agent.EndPoint.Port, cred, TimeSpan.FromSeconds(timeoutSeconds));

    public static TheoryData<SnmpAuthProtocol, SnmpPrivProtocol> AllCombinations()
    {
        var data = new TheoryData<SnmpAuthProtocol, SnmpPrivProtocol>();
        foreach (var a in Enum.GetValues<SnmpAuthProtocol>())
        {
            foreach (var p in Enum.GetValues<SnmpPrivProtocol>())
            {
                data.Add(a, p);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public async Task Get_round_trips_through_authPriv(SnmpAuthProtocol auth, SnmpPrivProtocol priv)
    {
        var cred = Cred(auth, priv);
        using var agent = Agent(cred);

        var result = await new SnmpV3Reader().GetAsync(Target(agent, cred), [SysDescr, SysName], Ct);

        Assert.Equal(1, agent.GetRequestsAnswered);
        Assert.Equal(FakeSnmpAgent.EngineId.ToHexString(), result.Engine.EngineId);
        Assert.Equal(FakeSnmpAgent.Boots, result.Engine.Boots);
        Assert.Equal("Cisco IOS XE Software, Version 17.12.04", result.Values[0].Text);
        Assert.Equal("lab-rtr-hub-01", result.Values[1].Text);
        Assert.All(result.Values, v => Assert.False(v.IsMissing));
    }

    [Fact]
    public async Task Missing_object_is_flagged_not_thrown()
    {
        var cred = Cred(SnmpAuthProtocol.Sha256, SnmpPrivProtocol.Aes128);
        using var agent = Agent(cred);

        var result = await new SnmpV3Reader().GetAsync(Target(agent, cred), ["1.3.6.1.2.1.1.4.0"], Ct);

        Assert.True(result.Values.Single().IsMissing);
    }

    [Theory]
    [InlineData(UsmReport.UnknownUserNames, FailureCategory.UnknownUser)]
    [InlineData(UsmReport.WrongDigests, FailureCategory.AuthFailure)]
    [InlineData(UsmReport.DecryptionErrors, FailureCategory.DecryptionError)]
    [InlineData(UsmReport.UnsupportedSecLevels, FailureCategory.UnsupportedSecurityLevel)]
    [InlineData(UsmReport.UnknownEngineIds, FailureCategory.UnknownEngine)]
    public async Task Usm_reports_are_classified(string reportOid, FailureCategory expected)
    {
        var cred = Cred(SnmpAuthProtocol.Sha256, SnmpPrivProtocol.Aes256);
        using var agent = Agent(cred);
        agent.ReportOid = reportOid;

        var ex = await Assert.ThrowsAsync<SnmpQueryException>(() => new SnmpV3Reader().GetAsync(Target(agent, cred), [SysDescr], Ct));

        Assert.Equal(expected, ex.Category);
    }

    [Fact]
    public async Task Not_in_time_window_is_retried_once_then_reported()
    {
        var cred = Cred(SnmpAuthProtocol.Sha256, SnmpPrivProtocol.Aes128);
        using var agent = Agent(cred);
        agent.ReportOid = UsmReport.NotInTimeWindows;

        var ex = await Assert.ThrowsAsync<SnmpQueryException>(() => new SnmpV3Reader().GetAsync(Target(agent, cred), [SysDescr], Ct));

        Assert.Equal(FailureCategory.NotInTimeWindow, ex.Category);
    }

    [Fact]
    public async Task Wrong_auth_password_gets_wrong_digests_report()
    {
        using var agent = Agent(Cred(SnmpAuthProtocol.Sha256, SnmpPrivProtocol.Aes128));
        var wrong = Cred(SnmpAuthProtocol.Sha256, SnmpPrivProtocol.Aes128, authPass: "not-the-auth-pass");

        var ex = await Assert.ThrowsAsync<SnmpQueryException>(() => new SnmpV3Reader().GetAsync(Target(agent, wrong), [SysDescr], Ct));

        Assert.Equal(FailureCategory.AuthFailure, ex.Category);
        Assert.Equal(1, agent.WrongDigestsSent);
        Assert.Equal(0, agent.GetRequestsAnswered);
    }

    [Fact]
    public async Task Wrong_privacy_password_is_not_answered_with_data()
    {
        using var agent = Agent(Cred(SnmpAuthProtocol.Sha256, SnmpPrivProtocol.Aes256));
        var wrong = Cred(SnmpAuthProtocol.Sha256, SnmpPrivProtocol.Aes256, privPass: "not-the-priv-pass");

        await Assert.ThrowsAsync<SnmpQueryException>(() => new SnmpV3Reader().GetAsync(Target(agent, wrong, 1), [SysDescr], Ct));

        Assert.Equal(0, agent.GetRequestsAnswered);
    }

    [Theory]
    [InlineData(SnmpAuthProtocol.Sha256)]
    [InlineData(SnmpAuthProtocol.Sha512)]
    public async Task Response_with_a_bad_hmac_is_discarded(SnmpAuthProtocol auth)
    {
        // SharpSnmpLib itself accepts this response; UsmAuthentication must catch it.
        var cred = Cred(auth, SnmpPrivProtocol.Aes128);
        using var agent = Agent(cred);
        agent.SignResponsesWithAuthPassword = "forger-auth-0001";

        var ex = await Assert.ThrowsAsync<SnmpQueryException>(() => new SnmpV3Reader().GetAsync(Target(agent, cred), [SysDescr], Ct));

        Assert.Equal(1, agent.GetRequestsAnswered);
        Assert.Equal(FailureCategory.AuthFailure, ex.Category);
        Assert.Contains("HMAC", ex.Message);
    }

    [Fact]
    public async Task Silence_after_discovery_is_a_timeout()
    {
        var cred = Cred(SnmpAuthProtocol.Sha256, SnmpPrivProtocol.Aes128);
        using var agent = Agent(cred);
        agent.SilentAfterDiscovery = true;

        var ex = await Assert.ThrowsAsync<SnmpQueryException>(() => new SnmpV3Reader().GetAsync(Target(agent, cred, 0.5), [SysDescr], Ct));

        Assert.Equal(FailureCategory.Timeout, ex.Category);
    }

    [Fact]
    public async Task Closed_port_is_refused_or_times_out()
    {
        // Grab a free UDP port, then close it so nothing listens there.
        int port;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }

        var target = new SnmpTarget(IPAddress.Loopback, port, Cred(SnmpAuthProtocol.Sha256, SnmpPrivProtocol.Aes128), TimeSpan.FromSeconds(1));
        var ex = await Assert.ThrowsAsync<SnmpQueryException>(() => new SnmpV3Reader().GetAsync(target, [SysDescr], Ct));

        // Windows reports ICMP port-unreachable on loopback as a reset; a firewall may just drop it.
        Assert.Contains(ex.Category, new[] { FailureCategory.Refused, FailureCategory.Timeout });
    }
}
