using System.Net;
using Yanmt.Core.Connectivity;
using Yanmt.Infrastructure.Credentials;

namespace Yanmt.Infrastructure.Collectors.Snmp;

/// <summary>
/// Read-only SNMPv3 access. This interface is the only way the app talks SNMP, and it has read
/// operations only: GET, GETNEXT, GETBULK and WALK. There is no SET, by design; a test fails if
/// a SET API is referenced anywhere in src/ or if this interface grows any other kind of method.
/// </summary>
public interface ISnmpReader
{
    /// <summary>GETs the given numeric OIDs in one request.</summary>
    /// <exception cref="SnmpQueryException">The request failed; <see cref="SnmpQueryException.Category"/> says why.</exception>
    Task<SnmpGetResult> GetAsync(SnmpTarget target, IReadOnlyList<string> oids, CancellationToken cancellationToken = default);

    /// <summary>GETNEXTs the given OIDs in one request: each result is the next object after its OID.</summary>
    Task<SnmpGetResult> GetNextAsync(SnmpTarget target, IReadOnlyList<string> oids, CancellationToken cancellationToken = default);

    /// <summary>One GETBULK (no non-repeaters): up to <paramref name="maxRepetitions"/> successors of each OID, interleaved.</summary>
    Task<SnmpGetResult> GetBulkAsync(SnmpTarget target, IReadOnlyList<string> oids, int maxRepetitions, CancellationToken cancellationToken = default);

    /// <summary>
    /// Walks one or more subtrees side by side with GETBULK (table columns walk in step, so a
    /// table costs one request per batch of rows). Each subtree stops at its end, at
    /// endOfMibView, or after <paramref name="maxRowsPerRoot"/> objects (then it is marked truncated).
    /// </summary>
    Task<SnmpWalkResult> WalkAsync(SnmpTarget target, IReadOnlyList<string> roots, int maxRowsPerRoot, CancellationToken cancellationToken = default);
}

public sealed record SnmpTarget(IPAddress Address, int Port, SnmpV3Credential Credential, TimeSpan Timeout);

/// <summary>
/// One varbind. <see cref="IsMissing"/> is set for noSuchObject/noSuchInstance/endOfMibView.
/// <see cref="Number"/> is the exact value of integer, counter, gauge and timeticks types (decimal
/// holds a full Counter64), and null for everything else.
/// </summary>
public sealed record SnmpValue(string Oid, string Type, string Text, bool IsMissing, decimal? Number = null);

/// <summary>The agent's authoritative engine, as learned during discovery or from the latest response.</summary>
public sealed record SnmpEngineInfo(string EngineId, int Boots, int Time);

public sealed record SnmpGetResult(IReadOnlyList<SnmpValue> Values, SnmpEngineInfo Engine);

/// <summary>The objects found under one walked root, in OID order.</summary>
public sealed record SnmpWalkColumn(string Root, IReadOnlyList<SnmpValue> Values, bool Truncated);

public sealed record SnmpWalkResult(IReadOnlyList<SnmpWalkColumn> Columns, SnmpEngineInfo Engine, int Requests)
{
    public SnmpWalkColumn this[string root] => Columns.First(c => c.Root == root);
}

public sealed class SnmpQueryException(FailureCategory category, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public FailureCategory Category { get; } = category;
}
