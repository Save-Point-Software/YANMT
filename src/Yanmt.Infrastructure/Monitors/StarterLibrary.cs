using Microsoft.EntityFrameworkCore;
using Yanmt.Core.Monitors;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Data.Entities;

namespace Yanmt.Infrastructure.Monitors;

/// <summary>
/// The shipped example monitors. They are ordinary, disabled definitions with no assignments: copy
/// or enable and apply them. Seeded once per library version, so deleting one doesn't bring it back.
/// </summary>
public static class StarterLibrary
{
    public const int Version = 1;
    private const string SeedSettingKey = "monitors.starter.version";

    private const string IfDescr = "1.3.6.1.2.1.2.2.1.2";
    private const string IfAdminStatus = "1.3.6.1.2.1.2.2.1.7";
    private const string IfStatusMap = "1=up, 2=down, 3=testing, 4=unknown, 5=dormant, 6=notPresent, 7=lowerLayerDown";

    /// <summary>Interface examples skip Null0 and friends, which are always "up" and carry nothing worth watching.</summary>
    private const string SkipNull = "^(?!Null)";

    public static IReadOnlyList<MonitorDefinition> Definitions(DateTime now) =>
    [
        new()
        {
            StarterKey = "if-oper-status",
            Name = "Interface oper status",
            Description = "ifOperStatus of every admin-up interface (or the ones you pick). Down is Critical; anything other than up or down is Warning.",
            OidMode = OidMode.Table, Oid = "1.3.6.1.2.1.2.2.1.8", LabelOid = IfDescr,
            ValueMap = IfStatusMap,
            RulesJson = Rules(new(RuleOperator.Equal, "up", null, Severity.Normal), new(RuleOperator.Equal, "down", null, Severity.Critical),
                new(RuleOperator.Equal, "lowerLayerDown", null, Severity.Critical)),
            DefaultState = Severity.Warning, ConsecutiveMatches = 2,
            FilterOid = IfAdminStatus, FilterOperator = RuleOperator.Equal, FilterValue = "1", LabelRegex = SkipNull,
            CreatedUtc = now, UpdatedUtc = now,
        },
        new()
        {
            StarterKey = "if-util-in",
            Name = "Interface utilization in %",
            Description = "Inbound utilization: rate of ifHCInOctets x 8 as a percentage of ifHighSpeed x 1,000,000.",
            OidMode = OidMode.Table, Oid = "1.3.6.1.2.1.31.1.1.1.6", LabelOid = IfDescr,
            ValueMode = ValueMode.Rate, Scale = 8, DenominatorOid = "1.3.6.1.2.1.31.1.1.1.15", DenominatorScale = 1_000_000, Unit = "%",
            RulesJson = Rules(new(RuleOperator.GreaterOrEqual, "90", null, Severity.Critical), new(RuleOperator.GreaterOrEqual, "75", null, Severity.Warning)),
            DefaultState = Severity.Normal, ConsecutiveMatches = 2, RaiseEvents = true,
            FilterOid = IfAdminStatus, FilterOperator = RuleOperator.Equal, FilterValue = "1", LabelRegex = SkipNull,
            CreatedUtc = now, UpdatedUtc = now,
        },
        new()
        {
            StarterKey = "if-util-out",
            Name = "Interface utilization out %",
            Description = "Outbound utilization: rate of ifHCOutOctets x 8 as a percentage of ifHighSpeed x 1,000,000.",
            OidMode = OidMode.Table, Oid = "1.3.6.1.2.1.31.1.1.1.10", LabelOid = IfDescr,
            ValueMode = ValueMode.Rate, Scale = 8, DenominatorOid = "1.3.6.1.2.1.31.1.1.1.15", DenominatorScale = 1_000_000, Unit = "%",
            RulesJson = Rules(new(RuleOperator.GreaterOrEqual, "90", null, Severity.Critical), new(RuleOperator.GreaterOrEqual, "75", null, Severity.Warning)),
            DefaultState = Severity.Normal, ConsecutiveMatches = 2,
            FilterOid = IfAdminStatus, FilterOperator = RuleOperator.Equal, FilterValue = "1", LabelRegex = SkipNull,
            CreatedUtc = now, UpdatedUtc = now,
        },
        new()
        {
            StarterKey = "if-in-errors",
            Name = "Interface input errors per second",
            Description = "Rate of ifInErrors. Any errors are Warning; 10 or more per second is Critical.",
            OidMode = OidMode.Table, Oid = "1.3.6.1.2.1.2.2.1.14", LabelOid = IfDescr,
            ValueMode = ValueMode.Rate, Unit = "errors/s",
            RulesJson = Rules(new(RuleOperator.GreaterOrEqual, "10", null, Severity.Critical), new(RuleOperator.Greater, "0", null, Severity.Warning)),
            DefaultState = Severity.Normal, ConsecutiveMatches = 2,
            FilterOid = IfAdminStatus, FilterOperator = RuleOperator.Equal, FilterValue = "1", LabelRegex = SkipNull,
            CreatedUtc = now, UpdatedUtc = now,
        },
        new()
        {
            StarterKey = "bgp-peer-state",
            Name = "BGP peer state",
            Description = "bgpPeerState (BGP4-MIB) of every administratively started peer; the row index is the peer address. Anything but established is Critical.",
            OidMode = OidMode.Table, Oid = "1.3.6.1.2.1.15.3.1.2", // no label column: the index is the peer address
            ValueMap = "1=idle, 2=connect, 3=active, 4=opensent, 5=openconfirm, 6=established",
            RulesJson = Rules(new MonitorRule(RuleOperator.Equal, "established", null, Severity.Normal)),
            DefaultState = Severity.Critical, ConsecutiveMatches = 2,
            FilterOid = "1.3.6.1.2.1.15.3.1.3", FilterOperator = RuleOperator.Equal, FilterValue = "2",
            CreatedUtc = now, UpdatedUtc = now,
        },
        new()
        {
            StarterKey = "cpu-1min",
            Name = "CPU 1-minute average",
            Description = "cpmCPUTotal1minRev of every CPU, as an example of a CPU monitor with tighter timing than the built-in 5-minute one.",
            OidMode = OidMode.Table, Oid = "1.3.6.1.4.1.9.9.109.1.1.1.1.7", Unit = "%",
            RulesJson = Rules(new(RuleOperator.GreaterOrEqual, "90", null, Severity.Critical), new(RuleOperator.GreaterOrEqual, "80", null, Severity.Warning)),
            DefaultState = Severity.Normal, ConsecutiveMatches = 3, IntervalSeconds = 60,
            CreatedUtc = now, UpdatedUtc = now,
        },
    ];

    private static string Rules(params MonitorRule[] rules) => MonitorMapping.RulesToJson(rules);

    /// <summary>The library as specs (numbered 1..n), for trying it against a device without a database.</summary>
    public static IReadOnlyList<MonitorSpec> Specs() =>
        Definitions(DateTime.UtcNow).Select((d, i) => MonitorMapping.ToSpec(d) with { Id = i + 1 }).ToList();

    /// <summary>Adds the library once per version; skips any whose name is already taken.</summary>
    public static async Task SeedAsync(IDbContextFactory<YanmtDbContext> factory, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var seeded = await db.Settings.FirstOrDefaultAsync(s => s.Key == SeedSettingKey, cancellationToken);
        if (seeded is not null && int.TryParse(seeded.JsonValue, out var version) && version >= Version)
        {
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var names = (await db.MonitorDefinitions.Select(d => d.Name).ToListAsync(cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        db.MonitorDefinitions.AddRange(Definitions(now).Where(d => !names.Contains(d.Name)));
        if (seeded is null)
        {
            db.Settings.Add(new Setting { Key = SeedSettingKey, JsonValue = Version.ToString(System.Globalization.CultureInfo.InvariantCulture), UpdatedUtc = now });
        }
        else
        {
            seeded.JsonValue = Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            seeded.UpdatedUtc = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
