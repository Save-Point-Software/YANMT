using System.Globalization;
using Yanmt.Core.Status;

namespace Yanmt.Core.Monitoring;

/// <summary>One memory pool row, from CISCO-ENHANCED-MEMPOOL-MIB (HC or 32-bit) or CISCO-MEMORY-POOL-MIB.</summary>
public sealed record MemoryPool(string Name, decimal? Used, decimal? Free, decimal? HcUsed = null, decimal? HcFree = null);

/// <summary>The pool memory is reported from, with its usage.</summary>
public sealed record MemoryUsage(string Pool, decimal Used, decimal Free)
{
    public double Percent => Used + Free == 0 ? 0 : (double)(Used / (Used + Free) * 100);
}

/// <summary>A threshold-evaluated reading: the state, the number for samples and charts, and the label text.</summary>
public sealed record StatReading(Severity State, double? Value, string ValueText, string Reason);

/// <summary>
/// The fixed default stats: CPU (5-minute average), memory, and uptime with reboot detection.
/// Nothing else is hard-coded; everything else is a custom monitor.
/// </summary>
public static class DefaultStats
{
    public const string ProcessorPool = "Processor";

    /// <summary>
    /// CPU is the busiest CPU's cpmCPUTotal5minRev (most IOS-XE boxes report one row). No
    /// dampening: the value is already a five-minute average.
    /// </summary>
    public static StatReading Cpu(IReadOnlyCollection<decimal> fiveMinutePercents, Thresholds thresholds)
    {
        var max = (double)fiveMinutePercents.Max();
        var text = Percent(max);
        var state = Evaluate(max, thresholds);
        var reason = fiveMinutePercents.Count > 1 ? $"busiest of {fiveMinutePercents.Count} CPUs at {text}" : $"CPU {text} (5-minute average)";
        return new(state, max, text, reason + ThresholdNote(state, thresholds));
    }

    /// <summary>
    /// Picks the "Processor" pool (else the first one), preferring the 64-bit HC counters when the
    /// agent fills them in. Null when no pool has usable numbers.
    /// </summary>
    public static MemoryUsage? PickMemory(IReadOnlyList<MemoryPool> pools)
    {
        static MemoryUsage? Usage(MemoryPool p) =>
            p is { HcUsed: { } hu, HcFree: { } hf } && hu + hf > 0 ? new(p.Name, hu, hf)
            : p is { Used: { } u, Free: { } f } && u + f > 0 ? new(p.Name, u, f)
            : null;

        var usable = pools.Select(Usage).OfType<MemoryUsage>().ToList();
        return usable.FirstOrDefault(u => string.Equals(u.Pool, ProcessorPool, StringComparison.OrdinalIgnoreCase)) ?? usable.FirstOrDefault();
    }

    public static StatReading Memory(MemoryUsage usage, Thresholds thresholds)
    {
        var text = Percent(usage.Percent);
        var state = Evaluate(usage.Percent, thresholds);
        return new(state, usage.Percent, text,
            $"{usage.Pool} pool {text} used ({Units.Bytes((double)usage.Used)} of {Units.Bytes((double)(usage.Used + usage.Free))})"
            + ThresholdNote(state, thresholds));
    }

    /// <summary>
    /// Uptime is Normal, except Warning on the poll that detects a reboot, so a reboot is visible
    /// on the map until the next poll.
    /// </summary>
    public static StatReading Uptime(uint ticks, bool rebooted) =>
        rebooted
            ? new(Severity.Warning, ticks / 100.0, Units.Uptime(ticks), $"rebooted (up {Units.Uptime(ticks)})")
            : new(Severity.Normal, ticks / 100.0, Units.Uptime(ticks), $"up {Units.Uptime(ticks)}");

    private static Severity Evaluate(double value, Thresholds t) =>
        value >= t.Critical ? Severity.Critical : value >= t.Warning ? Severity.Warning : Severity.Normal;

    private static string ThresholdNote(Severity state, Thresholds t) => state switch
    {
        Severity.Critical => $" (Critical at {t.Critical:0.#}%)",
        Severity.Warning => $" (Warning at {t.Warning:0.#}%)",
        _ => "",
    };

    private static string Percent(double v) => v.ToString(v < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + "%";
}

/// <summary>The previous poll's view of the agent, kept per node to spot reboots.</summary>
public sealed record UptimeBaseline(uint UptimeTicks, string? EngineId, int? EngineBoots);

public static class RebootDetector
{
    /// <summary>sysUpTime is TimeTicks (1/100 s) and wraps after about 497 days.</summary>
    private const double WrapZone = 0.9 * uint.MaxValue;

    /// <summary>
    /// A reboot is an increase in snmpEngineBoots on the same engine (the most reliable signal in
    /// SNMPv3), or sysUpTime going backwards when it wasn't near its 32-bit wrap. A changed engine
    /// ID means a different or re-initialised device, which isn't reported as a reboot.
    /// </summary>
    public static bool IsReboot(UptimeBaseline? previous, uint uptimeTicks, string? engineId, int? engineBoots)
    {
        if (previous is null)
        {
            return false;
        }

        var sameEngine = previous.EngineId is not null && previous.EngineId == engineId;
        if (previous.EngineId is not null && engineId is not null && !sameEngine)
        {
            return false;
        }

        if (sameEngine && previous.EngineBoots is { } before && engineBoots is { } now && now > before)
        {
            return true;
        }

        return uptimeTicks < previous.UptimeTicks && previous.UptimeTicks < WrapZone;
    }
}
