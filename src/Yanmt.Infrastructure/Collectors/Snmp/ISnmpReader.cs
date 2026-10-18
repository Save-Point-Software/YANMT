using System.Net;
using Yanmt.Core.Connectivity;
using Yanmt.Infrastructure.Credentials;

namespace Yanmt.Infrastructure.Collectors.Snmp;

/// <summary>
/// Read-only SNMPv3 access. This interface is the only way the app talks SNMP, and it has read
/// operations only: GET now, GETNEXT/GETBULK/WALK in Phase 3. There is no SET, by design; a test
/// fails if a SET API is referenced anywhere in src/.
/// </summary>
public interface ISnmpReader
{
    /// <summary>GETs the given numeric OIDs in one request.</summary>
    /// <exception cref="SnmpQueryException">The request failed; <see cref="SnmpQueryException.Category"/> says why.</exception>
    Task<SnmpGetResult> GetAsync(SnmpTarget target, IReadOnlyList<string> oids, CancellationToken cancellationToken = default);
}

public sealed record SnmpTarget(IPAddress Address, int Port, SnmpV3Credential Credential, TimeSpan Timeout);

/// <summary>One varbind. <see cref="IsMissing"/> is set for noSuchObject/noSuchInstance/endOfMibView.</summary>
public sealed record SnmpValue(string Oid, string Type, string Text, bool IsMissing);

/// <summary>The agent's authoritative engine, as learned during discovery.</summary>
public sealed record SnmpEngineInfo(string EngineId, int Boots, int Time);

public sealed record SnmpGetResult(IReadOnlyList<SnmpValue> Values, SnmpEngineInfo Engine);

public sealed class SnmpQueryException(FailureCategory category, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public FailureCategory Category { get; } = category;
}
