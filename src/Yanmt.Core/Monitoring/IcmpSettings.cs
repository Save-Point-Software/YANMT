namespace Yanmt.Core.Monitoring;

/// <summary>ICMP polling settings (admin-editable, stored in the DB).</summary>
public sealed record IcmpSettings
{
    /// <summary>Gap between echoes in one burst. Fixed: it only keeps a burst from looking like a flood.</summary>
    public const int EchoSpacingMs = 200;

    public int IntervalSeconds { get; init; } = 60;
    public int EchoCount { get; init; } = 4;
    public int TimeoutMs { get; init; } = 2000;

    /// <summary>Loss at or above this (but below 100%) is Warning. 50% means 2 of 4 echoes.</summary>
    public double WarningLossPercent { get; init; } = 50;

    /// <summary>Average round-trip time at or above this is Warning.</summary>
    public double WarningLatencyMs { get; init; } = 150;

    /// <summary>Consecutive polls with no reply at all before the state goes Critical (Warning until then).</summary>
    public int FailuresBeforeCritical { get; init; } = 3;

    /// <summary>Longest a burst can take: every echo timing out, plus the spacing between them.</summary>
    public TimeSpan WorstCaseBurst => TimeSpan.FromMilliseconds(EchoCount * TimeoutMs + (EchoCount - 1) * EchoSpacingMs);

    public IReadOnlyList<(string Field, string Error)> Validate()
    {
        var errors = new List<(string, string)>();
        void Range(string field, double value, double min, double max, string label)
        {
            if (value < min || value > max)
            {
                errors.Add((field, $"{label} must be between {min} and {max}."));
            }
        }

        Range(nameof(IntervalSeconds), IntervalSeconds, 10, 3600, "Interval (seconds)");
        Range(nameof(EchoCount), EchoCount, 1, 10, "Echoes per poll");
        Range(nameof(TimeoutMs), TimeoutMs, 100, 10000, "Timeout (ms)");
        Range(nameof(WarningLossPercent), WarningLossPercent, 1, 100, "Warning loss (%)");
        Range(nameof(WarningLatencyMs), WarningLatencyMs, 1, 60000, "Warning latency (ms)");
        Range(nameof(FailuresBeforeCritical), FailuresBeforeCritical, 1, 20, "Failures before Critical");

        if (errors.Count == 0 && WorstCaseBurst >= TimeSpan.FromSeconds(IntervalSeconds))
        {
            errors.Add((nameof(TimeoutMs),
                $"Echoes × timeout ({WorstCaseBurst.TotalSeconds:0.#} s worst case) must be shorter than the interval ({IntervalSeconds} s)."));
        }

        return errors;
    }
}
