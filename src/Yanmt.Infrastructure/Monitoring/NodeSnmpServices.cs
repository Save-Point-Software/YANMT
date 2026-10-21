using System.Net;
using Microsoft.EntityFrameworkCore;
using Yanmt.Core.Connectivity;
using Yanmt.Core.Monitoring;
using Yanmt.Core.Oids;
using Yanmt.Infrastructure.Collectors.Snmp;
using Yanmt.Infrastructure.Credentials;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Security;
using Yanmt.Infrastructure.Settings;

namespace Yanmt.Infrastructure.Monitoring;

/// <summary>Either an SNMP target for a node, or the reason there isn't one.</summary>
public sealed record SnmpTargetResult(SnmpTarget? Target, string? Error);

/// <summary>Builds the SNMP target for on-demand tools (interfaces tab, query tool) from the node's profile.</summary>
public sealed class SnmpTargetResolver(IDbContextFactory<YanmtDbContext> dbFactory, CredentialService credentials, SettingsService settings)
{
    public async Task<SnmpTargetResult> ResolveAsync(int nodeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var node = await db.Nodes.AsNoTracking().Where(n => n.Id == nodeId)
            .Select(n => new { n.ManagementIp, n.SnmpProfileId }).FirstOrDefaultAsync(cancellationToken);
        if (node is null)
        {
            return new(null, "The node no longer exists.");
        }

        if (node.SnmpProfileId is not { } profileId)
        {
            return new(null, "No SNMPv3 profile is assigned to this node.");
        }

        try
        {
            if (await credentials.LoadSnmpAsync(profileId, cancellationToken) is not var (credential, port))
            {
                return new(null, "The assigned SNMPv3 profile no longer exists.");
            }

            var timeout = TimeSpan.FromMilliseconds((await settings.GetAsync(SettingKeys.Snmp, cancellationToken)).TimeoutMs);
            return new(new SnmpTarget(IPAddress.Parse(node.ManagementIp), port, credential, timeout), null);
        }
        catch (SecretUnreadableException)
        {
            return new(null, "The SNMPv3 profile's passwords can't be decrypted on this server; re-enter them under Credentials.");
        }
    }
}

public sealed record InterfaceRow(
    string Index,
    string? Name,
    string? Description,
    string? Alias,
    int? AdminStatus,
    int? OperStatus,
    double? SpeedMbps,
    double? InBps,
    double? OutBps)
{
    public double? InUtilization => SpeedMbps is > 0 && InBps is { } b ? b / (SpeedMbps.Value * 1e6) * 100 : null;
    public double? OutUtilization => SpeedMbps is > 0 && OutBps is { } b ? b / (SpeedMbps.Value * 1e6) * 100 : null;

    public static string Status(int? status) => status switch
    {
        1 => "up",
        2 => "down",
        3 => "testing",
        4 => "unknown",
        5 => "dormant",
        6 => "notPresent",
        7 => "lowerLayerDown",
        null => "",
        _ => status.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

public sealed record InterfaceTable(IReadOnlyList<InterfaceRow> Rows, double SampleSeconds, bool Truncated, string? Error)
{
    public static InterfaceTable Fail(string error) => new([], 0, false, error);
}

/// <summary>
/// The live interfaces tab: walks IF-MIB on demand (never polled continuously) and takes two
/// counter samples a few seconds apart for current rates. 64-bit HC counters are used when the
/// agent has them, 32-bit ones (with wrap handling) otherwise.
/// </summary>
public sealed class InterfaceService(SnmpTargetResolver targets, ISnmpReader snmp, TimeProvider clock)
{
    public const int MaxInterfaces = 2000;
    public static readonly TimeSpan DefaultSampleGap = TimeSpan.FromSeconds(5);

    private const string IfDescr = "1.3.6.1.2.1.2.2.1.2";
    private const string IfSpeed = "1.3.6.1.2.1.2.2.1.5";
    private const string IfAdminStatus = "1.3.6.1.2.1.2.2.1.7";
    private const string IfOperStatus = "1.3.6.1.2.1.2.2.1.8";
    private const string IfInOctets = "1.3.6.1.2.1.2.2.1.10";
    private const string IfOutOctets = "1.3.6.1.2.1.2.2.1.16";
    private const string IfName = "1.3.6.1.2.1.31.1.1.1.1";
    private const string IfHcInOctets = "1.3.6.1.2.1.31.1.1.1.6";
    private const string IfHcOutOctets = "1.3.6.1.2.1.31.1.1.1.10";
    private const string IfHighSpeed = "1.3.6.1.2.1.31.1.1.1.15";
    private const string IfAlias = "1.3.6.1.2.1.31.1.1.1.18";

    private static readonly string[] Counters = [IfHcInOctets, IfHcOutOctets, IfInOctets, IfOutOctets];

    public async Task<InterfaceTable> ReadAsync(int nodeId, TimeSpan sampleGap, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var resolved = await targets.ResolveAsync(nodeId, cancellationToken);
        if (resolved.Target is not { } target)
        {
            return InterfaceTable.Fail(resolved.Error!);
        }

        try
        {
            progress?.Report("Walking IF-MIB…");
            var first = await snmp.WalkAsync(target,
                [IfDescr, IfSpeed, IfAdminStatus, IfOperStatus, IfName, IfAlias, IfHighSpeed, .. Counters], MaxInterfaces, cancellationToken);
            var firstAt = clock.GetTimestamp();

            progress?.Report($"Sampling counters again in {sampleGap.TotalSeconds:0} s for current rates…");
            await Task.Delay(sampleGap, clock, cancellationToken);
            var second = await snmp.WalkAsync(target, Counters, MaxInterfaces, cancellationToken);
            var seconds = clock.GetElapsedTime(firstAt).TotalSeconds;

            var descr = ByIndex(first[IfDescr]);
            var name = ByIndex(first[IfName]);
            var alias = ByIndex(first[IfAlias]);
            var admin = ByIndex(first[IfAdminStatus]);
            var oper = ByIndex(first[IfOperStatus]);
            var speed = ByIndex(first[IfSpeed]);
            var highSpeed = ByIndex(first[IfHighSpeed]);

            double? Rate(string hcColumn, string column, string index)
            {
                if (Num(ByIndex(first[hcColumn]), index) is { } a && Num(ByIndex(second[hcColumn]), index) is { } b)
                {
                    return RateMath.BitsPerSecond(a, b, 64, seconds);
                }

                return Num(ByIndex(first[column]), index) is { } c && Num(ByIndex(second[column]), index) is { } d
                    ? RateMath.BitsPerSecond(c, d, 32, seconds)
                    : null;
            }

            var indexes = descr.Keys.Union(name.Keys).Union(oper.Keys)
                .OrderBy(i => int.TryParse(i, out var n) ? n : int.MaxValue).ThenBy(i => i, StringComparer.Ordinal);
            var rows = indexes.Select(i => new InterfaceRow(
                    i,
                    Text(name, i),
                    Text(descr, i),
                    Text(alias, i),
                    (int?)Num(admin, i),
                    (int?)Num(oper, i),
                    Num(highSpeed, i) is { } hs and > 0 ? (double)hs : Num(speed, i) is { } s ? (double)s / 1e6 : null,
                    Rate(IfHcInOctets, IfInOctets, i),
                    Rate(IfHcOutOctets, IfOutOctets, i)))
                .ToList();
            return new InterfaceTable(rows, seconds, first.Columns.Any(c => c.Truncated), null);
        }
        catch (SnmpQueryException ex)
        {
            return InterfaceTable.Fail(ex.Message);
        }
    }

    private static Dictionary<string, SnmpValue> ByIndex(SnmpWalkColumn column) =>
        column.Values.ToDictionary(v => v.Oid.TrimStart('.')[(column.Root.Length + 1)..]);

    private static string? Text(Dictionary<string, SnmpValue> d, string index) => d.TryGetValue(index, out var v) ? v.Text : null;

    private static decimal? Num(Dictionary<string, SnmpValue> d, string index) => d.TryGetValue(index, out var v) ? v.Number : null;
}

public enum SnmpQueryOperation
{
    Get,
    GetNext,
    Walk,
}

public sealed record SnmpQueryRow(string Oid, string? Name, string Type, string Value);

public sealed record SnmpQueryResult(IReadOnlyList<SnmpQueryRow> Rows, bool Truncated, TimeSpan Elapsed, string? Error, string? ResolvedOid)
{
    public static SnmpQueryResult Fail(string error, string? oid = null) => new([], false, TimeSpan.Zero, error, oid);
}

/// <summary>
/// The node page's read-only query tool: GET, GETNEXT or WALK one OID (numeric or a known name).
/// It goes through <see cref="ISnmpReader"/> like everything else, so there is no SET path here either.
/// </summary>
public sealed class SnmpQueryService(SnmpTargetResolver targets, ISnmpReader snmp, IOidNameResolver names, TimeProvider clock)
{
    public const int MaxWalkRows = 2000;

    public async Task<SnmpQueryResult> QueryAsync(int nodeId, SnmpQueryOperation operation, string oidText, CancellationToken cancellationToken = default)
    {
        if (!names.TryParse(oidText, out var oid))
        {
            return SnmpQueryResult.Fail($"'{oidText}' isn't a numeric OID or a known object name.");
        }

        var resolved = await targets.ResolveAsync(nodeId, cancellationToken);
        if (resolved.Target is not { } target)
        {
            return SnmpQueryResult.Fail(resolved.Error!, oid);
        }

        var started = clock.GetTimestamp();
        try
        {
            IReadOnlyList<SnmpValue> values;
            var truncated = false;
            switch (operation)
            {
                case SnmpQueryOperation.Get:
                    values = (await snmp.GetAsync(target, [oid], cancellationToken)).Values;
                    break;
                case SnmpQueryOperation.GetNext:
                    values = (await snmp.GetNextAsync(target, [oid], cancellationToken)).Values;
                    break;
                default:
                    var column = (await snmp.WalkAsync(target, [oid], MaxWalkRows, cancellationToken)).Columns[0];
                    values = column.Values;
                    truncated = column.Truncated;
                    break;
            }

            var rows = values.Select(v => new SnmpQueryRow(v.Oid, names.Describe(v.Oid), v.Type, v.Text)).ToList();
            return new SnmpQueryResult(rows, truncated, clock.GetElapsedTime(started), null, oid);
        }
        catch (SnmpQueryException ex)
        {
            return new SnmpQueryResult([], false, clock.GetElapsedTime(started), ex.Message, oid);
        }
        catch (ArgumentException ex)
        {
            return SnmpQueryResult.Fail(ex.Message, oid);
        }
    }
}
