namespace Yanmt.Core.Monitoring;

/// <summary>Default SNMP stats polling settings (admin-editable, stored in the DB).</summary>
public sealed record SnmpSettings
{
    public int IntervalSeconds { get; init; } = 300;

    /// <summary>Per request. A failing request is retried once after rediscovering the engine.</summary>
    public int TimeoutMs { get; init; } = 5000;

    public double CpuWarningPercent { get; init; } = 80;
    public double CpuCriticalPercent { get; init; } = 90;
    public double MemoryWarningPercent { get; init; } = 80;
    public double MemoryCriticalPercent { get; init; } = 90;

    public Thresholds Cpu => new(CpuWarningPercent, CpuCriticalPercent);
    public Thresholds Memory => new(MemoryWarningPercent, MemoryCriticalPercent);

    public IReadOnlyList<(string Field, string Error)> Validate()
    {
        var errors = new List<(string, string)>();
        if (IntervalSeconds is < 60 or > 3600)
        {
            errors.Add((nameof(IntervalSeconds), "Interval (seconds) must be between 60 and 3600."));
        }

        if (TimeoutMs is < 500 or > 30000)
        {
            errors.Add((nameof(TimeoutMs), "Timeout (ms) must be between 500 and 30000."));
        }

        errors.AddRange(Cpu.Validate("CPU").Select(e => (nameof(CpuWarningPercent), e)));
        errors.AddRange(Memory.Validate("Memory").Select(e => (nameof(MemoryWarningPercent), e)));

        // Worst case per poll: a GET plus a few walk requests, each possibly retried once.
        if (errors.Count == 0 && TimeoutMs * 8L >= IntervalSeconds * 1000L)
        {
            errors.Add((nameof(TimeoutMs), "The timeout is too long for the interval: a slow node's poll could overlap the next one."));
        }

        return errors;
    }
}

/// <summary>A Warning/Critical pair for a percentage. Critical wins when both are reached.</summary>
public readonly record struct Thresholds(double Warning, double Critical)
{
    public IEnumerable<string> Validate(string label)
    {
        if (Warning is < 1 or > 100 || Critical is < 1 or > 100)
        {
            yield return $"{label} thresholds must be between 1 and 100%.";
        }
        else if (Warning >= Critical)
        {
            yield return $"{label} Warning must be below Critical.";
        }
    }

    /// <summary>Per-node values override the global ones field by field.</summary>
    public Thresholds Override(double? warning, double? critical) => new(warning ?? Warning, critical ?? Critical);
}
