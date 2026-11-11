using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yanmt.Core.Monitors;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Data.Entities;

namespace Yanmt.Infrastructure.Monitors;

/// <summary>
/// The shipped example monitors. They are ordinary, disabled definitions with no assignments: copy
/// or enable and apply them. Each one is added once, in the library version that introduced it, so
/// deleting one doesn't bring it back. Later versions can also adjust starters the user hasn't touched.
/// </summary>
public static class StarterLibrary
{
    /// <summary>
    /// 1: the original six. 2: BGP peer state and Interface oper status poll every 60 s (one table walk
    /// each), so an outage shows in about 2 minutes instead of 10.
    /// </summary>
    public const int Version = 2;

    /// <summary>The library version that first shipped each starter. A starter is only ever added by a seed that crosses it.</summary>
    private static readonly Dictionary<string, int> AddedIn = new(StringComparer.Ordinal)
    {
        ["if-oper-status"] = 1, ["if-util-in"] = 1, ["if-util-out"] = 1, ["if-in-errors"] = 1, ["bgp-peer-state"] = 1, ["cpu-1min"] = 1,
    };

    /// <summary>Starters whose interval version 2 changed to 60 s.</summary>
    private static readonly string[] FastPollStarters = ["bgp-peer-state", "if-oper-status"];
    private const int FastPollSeconds = 60;

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
            DefaultState = Severity.Warning, ConsecutiveMatches = 2, IntervalSeconds = FastPollSeconds,
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
            DefaultState = Severity.Critical, ConsecutiveMatches = 2, IntervalSeconds = FastPollSeconds,
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

    /// <summary>
    /// Brings the library up to <see cref="Version"/>: adds starters introduced since the last seed
    /// (skipping any whose name is taken), then applies each version's changes to existing starters.
    /// Changes are audited as the background user.
    /// </summary>
    public static async Task SeedAsync(IDbContextFactory<YanmtDbContext> factory, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var seeded = await db.Settings.FirstOrDefaultAsync(s => s.Key == SeedSettingKey, cancellationToken);
        var from = seeded is not null && int.TryParse(seeded.JsonValue, out var v) ? v : 0;
        if (from >= Version)
        {
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (from >= 1 && from < 2)
        {
            await UseFastPollingAsync(db, now, cancellationToken);
        }

        var names = (await db.MonitorDefinitions.Select(d => d.Name).ToListAsync(cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        db.MonitorDefinitions.AddRange(Definitions(now).Where(d => AddedIn[d.StarterKey!] > from && !names.Contains(d.Name)));
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

    /// <summary>
    /// Version 2: the BGP and oper-status starters poll every 60 s. Only where the interval is still
    /// blank (SNMP default) and the audit log shows nobody ever changed it, so a deliberate choice,
    /// including setting it back to blank, is kept. Copies have no starter key and are never touched.
    /// </summary>
    private static async Task UseFastPollingAsync(YanmtDbContext db, DateTime now, CancellationToken cancellationToken)
    {
        var candidates = await db.MonitorDefinitions
            .Where(d => d.StarterKey != null && FastPollStarters.Contains(d.StarterKey) && d.IntervalSeconds == null)
            .ToListAsync(cancellationToken);
        foreach (var d in candidates)
        {
            if (!await IntervalWasEditedAsync(db, d.Id, cancellationToken))
            {
                d.IntervalSeconds = FastPollSeconds;
                d.UpdatedUtc = now;
            }
        }
    }

    internal static async Task<bool> IntervalWasEditedAsync(YanmtDbContext db, int definitionId, CancellationToken cancellationToken)
    {
        var key = definitionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var changes = await db.AuditEntries.AsNoTracking()
            .Where(a => a.EntityType == nameof(MonitorDefinition) && a.EntityKey == key && a.Action == "Update" && a.Changes != null)
            .Select(a => a.Changes!)
            .ToListAsync(cancellationToken);
        return changes.Any(ChangesInterval);
    }

    /// <summary>True if an Update audit's changes JSON (<c>{"Prop":{"old":..,"new":..}}</c>) includes the interval.</summary>
    private static bool ChangesInterval(string changesJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(changesJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(nameof(MonitorDefinition.IntervalSeconds), out _);
        }
        catch (JsonException)
        {
            return true; // unreadable history: assume the user may have chosen it, and leave it alone
        }
    }
}
