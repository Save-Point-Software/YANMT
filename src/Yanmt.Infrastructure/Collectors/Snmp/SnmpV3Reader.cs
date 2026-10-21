using System.Collections.Concurrent;
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
/// SNMPv3 authPriv reader on SharpSnmpLib. Only GetRequest, GetNextRequest and GetBulkRequest
/// messages are ever built here. Every response goes through one exchange path that checks it is
/// an authenticated, encrypted response from the expected engine and user with a valid HMAC.
///
/// Engine discovery is cached per agent endpoint, with the agent's clock extrapolated locally, so
/// a poll or a walk costs one round trip per request instead of two. A notInTimeWindow or
/// unknownEngineID report, or a timeout on a cached engine, refreshes the cache and retries once.
/// </summary>
public sealed class SnmpV3Reader(TimeProvider clock) : ISnmpReader
{
    /// <summary>Hard ceiling for one walked root, whatever the caller asks for.</summary>
    public const int MaxWalkRows = 10_000;

    /// <summary>Varbinds asked for per GETBULK across all walked roots; keeps replies near one UDP datagram.</summary>
    private const int BulkVarbindBudget = 48;

    private static readonly TimeSpan EngineCacheLifetime = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, CachedEngine> _engines = new();

    public SnmpV3Reader()
        : this(TimeProvider.System)
    {
    }

    private sealed record CachedEngine(OctetString EngineId, int Boots, int Time, long ObtainedAt);

    private enum Operation
    {
        Get,
        GetNext,
        GetBulk,
    }

    public async Task<SnmpGetResult> GetAsync(SnmpTarget target, IReadOnlyList<string> oids, CancellationToken cancellationToken = default)
    {
        var (vars, engine) = await RequestAsync(target, Operation.Get, Parse(oids), 0, cancellationToken);
        return new SnmpGetResult(vars.Select(ToValue).ToList(), engine);
    }

    public async Task<SnmpGetResult> GetNextAsync(SnmpTarget target, IReadOnlyList<string> oids, CancellationToken cancellationToken = default)
    {
        var (vars, engine) = await RequestAsync(target, Operation.GetNext, Parse(oids), 0, cancellationToken);
        return new SnmpGetResult(vars.Select(ToValue).ToList(), engine);
    }

    public async Task<SnmpGetResult> GetBulkAsync(SnmpTarget target, IReadOnlyList<string> oids, int maxRepetitions, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRepetitions, 1);
        var (vars, engine) = await RequestAsync(target, Operation.GetBulk, Parse(oids), maxRepetitions, cancellationToken);
        return new SnmpGetResult(vars.Select(ToValue).ToList(), engine);
    }

    public async Task<SnmpWalkResult> WalkAsync(SnmpTarget target, IReadOnlyList<string> roots, int maxRowsPerRoot, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(roots.Count);
        var limit = Math.Clamp(maxRowsPerRoot, 1, MaxWalkRows);
        var rootIds = Parse(roots).Select(v => v.Id).ToList();
        var rootNumbers = rootIds.Select(id => id.ToNumerical()).ToList();
        var rows = rootIds.Select(_ => new List<SnmpValue>()).ToList();
        var position = rootIds.ToList();
        var truncated = new bool[rootIds.Count];
        var active = Enumerable.Range(0, rootIds.Count).ToList();
        SnmpEngineInfo? engine = null;
        var requests = 0;

        while (active.Count > 0)
        {
            var repetitions = Math.Clamp(BulkVarbindBudget / active.Count, 1, limit);
            var (vars, info) = await RequestAsync(target, Operation.GetBulk,
                active.Select(i => new Variable(position[i])).ToList(), repetitions, cancellationToken);
            engine = info;
            requests++;
            if (vars.Count == 0)
            {
                foreach (var i in active)
                {
                    truncated[i] = true; // the agent returned nothing at all; don't spin
                }

                break;
            }

            // Varbinds come back interleaved: repetition r of root k is at r * active.Count + k.
            var finished = new HashSet<int>();
            for (var n = 0; n < vars.Count; n++)
            {
                var i = active[n % active.Count];
                if (finished.Contains(i))
                {
                    continue;
                }

                var v = vars[n];
                var missing = v.Data.TypeCode is SnmpType.EndOfMibView or SnmpType.NoSuchObject or SnmpType.NoSuchInstance;
                if (missing || !IsUnder(v.Id.ToNumerical(), rootNumbers[i]) || v.Id.CompareTo(position[i]) <= 0)
                {
                    // Past the subtree, at the end of the MIB, or an agent that didn't move forward.
                    finished.Add(i);
                    continue;
                }

                rows[i].Add(ToValue(v));
                position[i] = v.Id;
                if (rows[i].Count >= limit)
                {
                    truncated[i] = true;
                    finished.Add(i);
                }
            }

            active = active.Where(i => !finished.Contains(i)).ToList();
        }

        var columns = roots.Select((r, i) => new SnmpWalkColumn(rootIds[i].ToString(), rows[i], truncated[i])).ToList();
        return new SnmpWalkResult(columns, engine!, requests);
    }

    private static bool IsUnder(uint[] oid, uint[] root) =>
        oid.Length > root.Length && oid.AsSpan(0, root.Length).SequenceEqual(root);

    /// <summary>
    /// Sends one read request and returns the verified response's varbinds. Handles discovery
    /// (cached), the retry cases, and maps every failure to a <see cref="SnmpQueryException"/>.
    /// </summary>
    private async Task<(IList<Variable> Vars, SnmpEngineInfo Engine)> RequestAsync(
        SnmpTarget target, Operation operation, IList<Variable> variables, int maxRepetitions, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(variables.Count);
        var endpoint = new IPEndPoint(target.Address, target.Port);
        var key = endpoint.ToString();
        var privacy = CreatePrivacy(target.Credential);
        var user = new OctetString(target.Credential.Username);

        // True once this call has itself heard a discovery reply: a later timeout then points at
        // the privacy settings rather than reachability.
        var discoveredNow = false;

        try
        {
            ISnmpMessage report;
            if (_engines.TryGetValue(key, out var cached) && clock.GetElapsedTime(cached.ObtainedAt) < EngineCacheLifetime)
            {
                report = SyntheticReport(cached);
            }
            else
            {
                report = await DiscoverAsync(key, endpoint, target.Timeout, cancellationToken);
                discoveredNow = true;
            }

            for (var attempt = 0; ; attempt++)
            {
                ISnmpMessage request = operation switch
                {
                    Operation.Get => new GetRequestMessage(VersionCode.V3, Messenger.NextMessageId, Messenger.NextRequestId, user,
                        OctetString.Empty, variables, privacy, Messenger.MaxMessageSize, report),
                    Operation.GetNext => new GetNextRequestMessage(VersionCode.V3, Messenger.NextMessageId, Messenger.NextRequestId, user,
                        OctetString.Empty, variables, privacy, Messenger.MaxMessageSize, report),
                    _ => new GetBulkRequestMessage(VersionCode.V3, Messenger.NextMessageId, Messenger.NextRequestId, user,
                        OctetString.Empty, 0, maxRepetitions, variables, privacy, Messenger.MaxMessageSize, report),
                };

                byte[] datagram;
                ISnmpMessage response;
                try
                {
                    (datagram, response) = await WithTimeout(ct => ExchangeAsync(request, endpoint, user, privacy, ct), target.Timeout, cancellationToken);
                }
                catch (System.TimeoutException) when (!discoveredNow)
                {
                    // The cached engine may be stale (rebooted, replaced) or the device may be down:
                    // rediscover to tell the two apart before giving up.
                    _engines.TryRemove(key, out _);
                    report = await DiscoverAsync(key, endpoint, target.Timeout, cancellationToken);
                    discoveredNow = true;
                    continue;
                }

                // Reports are unauthenticated by design (they're how an agent says our credentials
                // are wrong), so they're only ever treated as errors or as fresh engine timing, never as data.
                if (response.TypeCode() == SnmpType.ReportPdu)
                {
                    var reportOid = response.Pdu().Variables.FirstOrDefault()?.Id.ToString() ?? "";
                    var (category, message) = UsmReport.Classify(reportOid);
                    if (category == FailureCategory.NotInTimeWindow && attempt < 2)
                    {
                        report = response; // carries the agent's current boots/time
                        Remember(key, response);
                        continue;
                    }

                    if (category == FailureCategory.UnknownEngine && !discoveredNow)
                    {
                        _engines.TryRemove(key, out _);
                        report = await DiscoverAsync(key, endpoint, target.Timeout, cancellationToken);
                        discoveredNow = true;
                        continue;
                    }

                    throw new SnmpQueryException(category, message);
                }

                var expectedEngine = report.Parameters?.EngineId?.ToHexString();
                if (response.TypeCode() != SnmpType.ResponsePdu
                    || !UsmAuthentication.IsAuthPriv(response)
                    || response.Parameters.UserName?.ToString() != target.Credential.Username
                    || response.Parameters.EngineId?.ToHexString() != expectedEngine
                    || !UsmAuthentication.Verify(datagram, response, target.Credential))
                {
                    throw new SnmpQueryException(FailureCategory.AuthFailure,
                        "The response failed SNMPv3 authentication (bad HMAC, wrong engine or user, or not authPriv), so it was discarded. " +
                        "Check the auth password and protocol; if they're right, something on the path is altering or spoofing replies.");
                }

                // Only an authenticated response may refresh the cached engine clock.
                Remember(key, response);
                var pdu = response.Pdu();
                if (pdu.ErrorStatus.ToInt32() != 0)
                {
                    throw new SnmpQueryException(FailureCategory.DeviceError,
                        $"The device returned error {pdu.ErrorStatus.ToErrorCode()} at varbind {pdu.ErrorIndex.ToInt32()}.");
                }

                var p = response.Parameters;
                return (pdu.Variables, new SnmpEngineInfo(p.EngineId!.ToHexString(), p.EngineBoots?.ToInt32() ?? 0, p.EngineTime?.ToInt32() ?? 0));
            }
        }
        catch (SnmpQueryException)
        {
            throw;
        }
        catch (System.TimeoutException ex) when (discoveredNow)
        {
            // Lab-verified on IOS-XE: a request with the wrong privacy password is dropped silently,
            // with no usmStatsDecryptionErrors report, so this is how that mistake shows up.
            throw new SnmpQueryException(FailureCategory.Timeout,
                $"{endpoint} answered SNMPv3 discovery but not the request within {target.Timeout.TotalSeconds:0.#} s. " +
                "This is most likely a wrong privacy password or privacy protocol (IOS-XE drops these silently, with no error report).", ex);
        }
        catch (DiscoveryTimeoutException ex)
        {
            throw new SnmpQueryException(FailureCategory.Timeout,
                $"No SNMP response from {endpoint} within {target.Timeout.TotalSeconds:0.#} s. Check reachability, the port, ACLs, and that SNMPv3 is enabled for this user.", ex);
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

    /// <summary>Thrown when discovery itself gets no answer, so the timeout isn't blamed on the privacy settings.</summary>
    private sealed class DiscoveryTimeoutException(Exception inner) : Exception("Discovery timed out.", inner);

    private async Task<ISnmpMessage> DiscoverAsync(string key, IPEndPoint endpoint, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ISnmpMessage report;
        try
        {
            report = await WithTimeout(ct => Messenger.GetNextDiscovery(SnmpType.GetRequestPdu).GetResponseAsync(endpoint, ct), timeout, cancellationToken);
        }
        catch (Exception ex) when (ex is System.TimeoutException or Lextm.SharpSnmpLib.Messaging.TimeoutException)
        {
            throw new DiscoveryTimeoutException(ex);
        }

        if (report.Parameters?.EngineId is not { } engineId || engineId.GetRaw().Length == 0)
        {
            throw new SnmpQueryException(FailureCategory.UnknownEngine, "The device's discovery report had no engine ID.");
        }

        Remember(key, report);
        return report;
    }

    private void Remember(string key, ISnmpMessage message)
    {
        var p = message.Parameters;
        if (p?.EngineId is { } engineId && engineId.GetRaw().Length > 0)
        {
            _engines[key] = new CachedEngine(engineId, p.EngineBoots?.ToInt32() ?? 0, p.EngineTime?.ToInt32() ?? 0, clock.GetTimestamp());
        }
    }

    /// <summary>A stand-in for the discovery report, with the agent's clock moved on by the time elapsed since.</summary>
    private ReportMessage SyntheticReport(CachedEngine engine)
    {
        var time = (int)Math.Min(int.MaxValue, engine.Time + (long)clock.GetElapsedTime(engine.ObtainedAt).TotalSeconds);
        return new ReportMessage(
            VersionCode.V3,
            new Header(new Integer32(0), new Integer32(Messenger.MaxMessageSize), 0),
            new SecurityParameters(engine.EngineId, new Integer32(engine.Boots), new Integer32(time), OctetString.Empty, OctetString.Empty, OctetString.Empty),
            new Scope(engine.EngineId, OctetString.Empty, new ReportPdu(0, ErrorCode.NoError, 0, new List<Variable>())),
            DefaultPrivacyProvider.DefaultPair,
            null);
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
        ISnmpMessage request, IPEndPoint endpoint, OctetString user, IPrivacyProvider privacy, CancellationToken cancellationToken)
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

    private static List<Variable> Parse(IReadOnlyList<string> oids)
    {
        ArgumentOutOfRangeException.ThrowIfZero(oids.Count);
        return oids.Select(o => new Variable(ParseOid(o))).ToList();
    }

    private static ObjectIdentifier ParseOid(string oid)
    {
        try
        {
            return new ObjectIdentifier(oid.Trim().TrimStart('.'));
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"'{oid}' is not a numeric OID.", nameof(oid), ex);
        }
    }

    private static SnmpValue ToValue(Variable v)
    {
        var data = v.Data;
        var type = data.TypeCode;
        var missing = type is SnmpType.NoSuchObject or SnmpType.NoSuchInstance or SnmpType.EndOfMibView;
        decimal? number = data switch
        {
            Integer32 i => i.ToInt32(),
            Counter32 c => c.ToUInt32(),
            Gauge32 g => g.ToUInt32(),
            TimeTicks t => t.ToUInt32(),
            Counter64 c => c.ToUInt64(),
            _ => null,
        };
        return new SnmpValue(v.Id.ToString(), type.ToString(), data.ToString() ?? "", missing, number);
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
