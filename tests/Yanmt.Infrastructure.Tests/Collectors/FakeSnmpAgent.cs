using System.Net;
using System.Net.Sockets;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using Yanmt.Infrastructure.Collectors.Snmp;
using Yanmt.Infrastructure.Credentials;

namespace Yanmt.Infrastructure.Tests.Collectors;

/// <summary>
/// A minimal SNMPv3 agent on loopback, enough to exercise <c>SnmpV3Reader</c> without a lab:
/// engine discovery, HMAC-checked authPriv GET responses from a fixed table, scripted USM reports,
/// or silence. Like a real agent, it answers a request with a bad HMAC with usmStatsWrongDigests.
/// It uses SharpSnmpLib's own crypto on both sides, so it proves our wiring and error handling,
/// not interoperability with IOS-XE (the CML integration tests do that).
/// </summary>
internal sealed class FakeSnmpAgent : IDisposable
{
    public static readonly OctetString EngineId = new(Convert.FromHexString("800000090300AABBCCDD0001"));
    public const int Boots = 7;

    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly UserRegistry _users = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly SnmpV3Credential _credential;
    private readonly byte[] _authKey;

    public FakeSnmpAgent(SnmpV3Credential credential, IReadOnlyDictionary<string, ISnmpData> values)
    {
        _credential = credential;
        _authKey = Rfc3414Keys.Localize(credential.Auth, credential.AuthPassword, EngineId.GetRaw());
        var user = credential.Username;
        var agentPrivacy = SnmpV3Reader.CreatePrivacy(credential);
        if (_users.Find(OctetString.Empty) is null)
        {
            _users.Add(OctetString.Empty, DefaultPrivacyProvider.DefaultPair); // discovery requests carry no user
        }

        _users.Add(new OctetString(user), agentPrivacy);
        Values = values;
        _loop = Task.Run(LoopAsync);
    }

    public IPEndPoint EndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;
    public IReadOnlyDictionary<string, ISnmpData> Values { get; }

    /// <summary>When set, answers every non-discovery request with a Report carrying this USM counter OID.</summary>
    public string? ReportOid { get; set; }

    /// <summary>Answer discovery, then stay silent (simulates an ACL drop or the wrong user being filtered).</summary>
    public bool SilentAfterDiscovery { get; set; }

    /// <summary>When set, responses are signed with this auth password instead of the real one (a forged reply).</summary>
    public string? SignResponsesWithAuthPassword { get; set; }

    /// <summary>Requests the agent couldn't parse/decrypt, with the library's exception.</summary>
    public List<Exception> RejectedRequests { get; } = [];

    public int GetRequestsAnswered { get; private set; }

    public int WrongDigestsSent { get; private set; }

    private int Time => (int)(Environment.TickCount64 / 1000 % int.MaxValue);

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await _udp.ReceiveAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue; // ICMP from a previous reply; ignore
            }

            ISnmpMessage request;
            try
            {
                request = MessageFactory.ParseMessages(packet.Buffer, _users)[0];
            }
            catch (Exception ex)
            {
                RejectedRequests.Add(ex);
                continue;
            }

            var reply = Respond(request, packet.Buffer);
            if (reply is not null)
            {
                await _udp.SendAsync(reply.ToBytes(), packet.RemoteEndPoint, _stop.Token);
            }
        }
    }

    private ISnmpMessage? Respond(ISnmpMessage request, byte[] datagram)
    {
        if (request.Parameters.EngineId is null || request.Parameters.EngineId.GetRaw().Length == 0)
        {
            return Report(request, "1.3.6.1.6.3.15.1.1.4.0"); // usmStatsUnknownEngineIDs: the discovery reply
        }

        if (SilentAfterDiscovery)
        {
            return null;
        }

        if (ReportOid is not null)
        {
            return Report(request, ReportOid);
        }

        // Checked with an independently derived key, as a real agent would: never the library's cached one.
        if (!UsmAuthentication.Verify(datagram, request, _credential.Auth, _authKey))
        {
            WrongDigestsSent++;
            return Report(request, "1.3.6.1.6.3.15.1.1.5.0"); // usmStatsWrongDigests
        }

        var privacy = SignResponsesWithAuthPassword is null
            ? _users.Find(request.Parameters.UserName)!
            : SnmpV3Reader.CreatePrivacy(new SnmpV3Credential(_credential.Username, _credential.Auth, SignResponsesWithAuthPassword,
                _credential.Priv, _credential.PrivPassword));
        var variables = request.Pdu().Variables
            .Select(v => new Variable(v.Id, Values.TryGetValue(v.Id.ToString(), out var data) ? data : new NoSuchObject()))
            .ToList();
        GetRequestsAnswered++;
        return new ResponseMessage(
            VersionCode.V3,
            new Header(new Integer32(request.MessageId()), new Integer32(Messenger.MaxMessageSize), privacy.ToSecurityLevel()),
            new SecurityParameters(EngineId, new Integer32(Boots), new Integer32(Time), request.Parameters.UserName,
                privacy.AuthenticationProvider.CleanDigest, privacy.Salt),
            new Scope(EngineId, OctetString.Empty, new ResponsePdu(request.RequestId(), ErrorCode.NoError, 0, variables)),
            privacy,
            true,
            null);
    }

    private ReportMessage Report(ISnmpMessage request, string oid) => new(
        VersionCode.V3,
        new Header(new Integer32(request.MessageId()), new Integer32(Messenger.MaxMessageSize), 0),
        new SecurityParameters(EngineId, new Integer32(Boots), new Integer32(Time), OctetString.Empty, OctetString.Empty, OctetString.Empty),
        new Scope(EngineId, OctetString.Empty,
            new ReportPdu(request.RequestId(), ErrorCode.NoError, 0, [new Variable(new ObjectIdentifier(oid), new Counter32(1))])),
        DefaultPrivacyProvider.DefaultPair,
        null);

    public void Dispose()
    {
        _stop.Cancel();
        _udp.Dispose();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // stopping
        }

        _stop.Dispose();
    }
}
