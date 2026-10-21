using System.Net;
using System.Net.Sockets;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using Yanmt.Core.Connectivity;
using Yanmt.Core.Credentials;
using Yanmt.Infrastructure.Credentials;

namespace Yanmt.Infrastructure.Collectors.Snmp;

/// <summary>
/// SNMPv3 authPriv reader on SharpSnmpLib. Only GetRequestMessage is ever built here.
/// Engine discovery runs per call for now; Phase 3 adds a per-node engine cache for polling.
/// </summary>
public sealed class SnmpV3Reader : ISnmpReader
{
    public async Task<SnmpGetResult> GetAsync(SnmpTarget target, IReadOnlyList<string> oids, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(oids.Count);
        var variables = oids.Select(o => new Variable(ParseOid(o))).ToList();
        var endpoint = new IPEndPoint(target.Address, target.Port);
        var privacy = CreatePrivacy(target.Credential);
        var user = new OctetString(target.Credential.Username);
        var discovered = false;

        try
        {
            ISnmpMessage report = await WithTimeout(
                ct => Messenger.GetNextDiscovery(SnmpType.GetRequestPdu).GetResponseAsync(endpoint, ct), target.Timeout, cancellationToken);
            var engineParameters = report.Parameters
                ?? throw new SnmpQueryException(FailureCategory.UnknownEngine, "The device's discovery report had no security parameters.");
            discovered = true;
            var engine = new SnmpEngineInfo(
                engineParameters.EngineId?.ToHexString() ?? "", engineParameters.EngineBoots?.ToInt32() ?? 0, engineParameters.EngineTime?.ToInt32() ?? 0);

            // One retry on notInTimeWindow: the report carries the agent's current boots/time.
            for (var attempt = 0; ; attempt++)
            {
                var request = new GetRequestMessage(
                    VersionCode.V3, Messenger.NextMessageId, Messenger.NextRequestId, user, OctetString.Empty,
                    variables, privacy, Messenger.MaxMessageSize, report);
                var (datagram, response) = await WithTimeout(
                    ct => ExchangeAsync(request, endpoint, user, privacy, ct), target.Timeout, cancellationToken);

                // Reports are unauthenticated by design (they're how an agent says our credentials
                // are wrong), so they're only ever treated as errors, never as data.
                if (response.TypeCode() == SnmpType.ReportPdu)
                {
                    var reportOid = response.Pdu().Variables.FirstOrDefault()?.Id.ToString() ?? "";
                    var (category, message) = UsmReport.Classify(reportOid);
                    if (category == FailureCategory.NotInTimeWindow && attempt == 0)
                    {
                        report = response;
                        continue;
                    }

                    throw new SnmpQueryException(category, message);
                }

                if (response.TypeCode() != SnmpType.ResponsePdu
                    || !UsmAuthentication.IsAuthPriv(response)
                    || response.Parameters.UserName?.ToString() != target.Credential.Username
                    || response.Parameters.EngineId?.ToHexString() != engine.EngineId
                    || !UsmAuthentication.Verify(datagram, response, target.Credential))
                {
                    throw new SnmpQueryException(FailureCategory.AuthFailure,
                        "The response failed SNMPv3 authentication (bad HMAC, wrong engine or user, or not authPriv), so it was discarded. " +
                        "Check the auth password and protocol; if they're right, something on the path is altering or spoofing replies.");
                }

                var pdu = response.Pdu();
                if (pdu.ErrorStatus.ToInt32() != 0)
                {
                    throw new SnmpQueryException(FailureCategory.DeviceError,
                        $"The device returned error {pdu.ErrorStatus.ToErrorCode()} at varbind {pdu.ErrorIndex.ToInt32()}.");
                }

                return new SnmpGetResult(pdu.Variables.Select(ToValue).ToList(), engine);
            }
        }
        catch (SnmpQueryException)
        {
            throw;
        }
        catch (System.TimeoutException ex) when (discovered)
        {
            // Lab-verified on IOS-XE: a request with the wrong privacy password is dropped silently,
            // with no usmStatsDecryptionErrors report, so this is how that mistake shows up.
            throw new SnmpQueryException(FailureCategory.Timeout,
                $"{endpoint} answered SNMPv3 discovery but not the request within {target.Timeout.TotalSeconds:0.#} s. " +
                "This is most likely a wrong privacy password or privacy protocol (IOS-XE drops these silently, with no error report).", ex);
        }
        catch (System.TimeoutException ex)
        {
            throw new SnmpQueryException(FailureCategory.Timeout,
                $"No SNMP response from {endpoint} within {target.Timeout.TotalSeconds:0.#} s. Check reachability, the port, ACLs, and that SNMPv3 is enabled for this user.", ex);
        }
        catch (Lextm.SharpSnmpLib.Messaging.TimeoutException ex)
        {
            throw new SnmpQueryException(FailureCategory.Timeout, $"No SNMP response from {endpoint}.", ex);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            // Windows surfaces an ICMP port-unreachable for UDP as WSAECONNRESET on the next receive.
            throw new SnmpQueryException(FailureCategory.Refused,
                $"{endpoint} answered with ICMP port unreachable: no SNMP agent is listening on UDP {target.Port}.", ex);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.HostUnreachable or SocketError.NetworkUnreachable)
        {
            throw new SnmpQueryException(FailureCategory.Unreachable, $"{endpoint} is unreachable ({ex.SocketErrorCode}).", ex);
        }
        catch (SocketException ex)
        {
            throw new SnmpQueryException(FailureCategory.Other, $"Socket error talking to {endpoint}: {ex.SocketErrorCode}.", ex);
        }
        catch (DecryptionException ex)
        {
            throw new SnmpQueryException(FailureCategory.DecryptionError,
                "The device's response couldn't be decrypted: wrong privacy password or privacy protocol.", ex);
        }
        catch (Exception ex) when (ex is SnmpException or OperationException)
        {
            throw new SnmpQueryException(FailureCategory.Other, $"SNMP error from {endpoint}: {ex.Message}", ex);
        }
    }

    internal static IPrivacyProvider CreatePrivacy(SnmpV3Credential credential)
    {
        // SHA-1 with AES-192/256 would need a non-standard key extension; refuse rather than guess.
        if (CredentialRules.ValidateSnmpProtocols(credential.Auth, credential.Priv) is { } pairing)
        {
            throw new SnmpQueryException(FailureCategory.UnsupportedAlgorithm, pairing);
        }

        IAuthenticationProvider auth = credential.Auth switch
        {
#pragma warning disable CS0618 // Legacy SHA-1 is a deliberate, UI-flagged option for devices without SHA-2 (docs/SPEC.md).
            SnmpAuthProtocol.Sha1 => new SHA1AuthenticationProvider(new OctetString(credential.AuthPassword)),
#pragma warning restore CS0618
            SnmpAuthProtocol.Sha256 => new SHA256AuthenticationProvider(new OctetString(credential.AuthPassword)),
            SnmpAuthProtocol.Sha384 => new SHA384AuthenticationProvider(new OctetString(credential.AuthPassword)),
            SnmpAuthProtocol.Sha512 => new SHA512AuthenticationProvider(new OctetString(credential.AuthPassword)),
            _ => throw new ArgumentOutOfRangeException(nameof(credential), credential.Auth, "Unsupported auth protocol."),
        };

        return credential.Priv switch
        {
            SnmpPrivProtocol.Aes128 => new AESPrivacyProvider(new OctetString(credential.PrivPassword), auth),
            SnmpPrivProtocol.Aes192 => new AES192PrivacyProvider(new OctetString(credential.PrivPassword), auth),
            SnmpPrivProtocol.Aes256 => new AES256PrivacyProvider(new OctetString(credential.PrivPassword), auth),
            _ => throw new ArgumentOutOfRangeException(nameof(credential), credential.Priv, "Unsupported privacy protocol."),
        };
    }

    /// <summary>
    /// Sends the request and returns the raw reply with its parse. Done here rather than with the
    /// library's GetResponseAsync because the HMAC check needs the exact bytes received.
    /// Datagrams from other addresses or with another message id are ignored.
    /// </summary>
    private static async Task<(byte[] Datagram, ISnmpMessage Message)> ExchangeAsync(
        GetRequestMessage request, IPEndPoint endpoint, OctetString user, IPrivacyProvider privacy, CancellationToken cancellationToken)
    {
        var registry = new UserRegistry();
        registry.Add(user, privacy);
        using var udp = new UdpClient(endpoint.AddressFamily);
        await udp.SendAsync(request.ToBytes(), endpoint, cancellationToken);
        while (true)
        {
            var received = await udp.ReceiveAsync(cancellationToken);
            if (!received.RemoteEndPoint.Equals(endpoint))
            {
                continue;
            }

            ISnmpMessage message;
            try
            {
                message = MessageFactory.ParseMessages(received.Buffer, registry)[0];
            }
            catch (Exception ex) when (ex is DecryptionException or SnmpException or ArgumentException or InvalidOperationException)
            {
                throw new SnmpQueryException(FailureCategory.DecryptionError,
                    "The device's response couldn't be decrypted or parsed: wrong privacy password or privacy protocol.", ex);
            }

            if (message.MessageId() == request.MessageId())
            {
                return (received.Buffer, message);
            }
        }
    }

    private static ObjectIdentifier ParseOid(string oid)
    {
        try
        {
            return new ObjectIdentifier(oid.TrimStart('.'));
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"'{oid}' is not a numeric OID.", nameof(oid), ex);
        }
    }

    private static SnmpValue ToValue(Variable v)
    {
        var type = v.Data.TypeCode;
        var missing = type is SnmpType.NoSuchObject or SnmpType.NoSuchInstance or SnmpType.EndOfMibView;
        return new SnmpValue(v.Id.ToString(), type.ToString(), v.Data.ToString() ?? "", missing);
    }

    // The library's own cancellation handling varies by call; WaitAsync guarantees the deadline.
    private static async Task<T> WithTimeout<T>(Func<CancellationToken, Task<T>> call, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            return await call(cts.Token).WaitAsync(timeout, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new System.TimeoutException();
        }
    }
}
