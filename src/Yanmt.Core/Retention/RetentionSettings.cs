namespace Yanmt.Core.Retention;

/// <summary>How long data is kept. Admin-editable; the nightly cleanup applies it.</summary>
public sealed record RetentionSettings
{
    public const int MaxDays = 3650;
    public const int MaxKeepVersions = 1000;

    /// <summary>Raw ICMP and metric samples. Only purged once they have been rolled up.</summary>
    public int RawSampleDays { get; init; } = 7;

    /// <summary>Hourly min/avg/max rollups.</summary>
    public int RollupDays { get; init; } = 90;

    /// <summary>Events (acknowledged or not).</summary>
    public int EventDays { get; init; } = 365;

    /// <summary>Keep only the newest N config versions per node. Null (default) keeps all. The latest is never deleted.</summary>
    public int? KeepConfigVersions { get; init; }

    public IReadOnlyList<(string Field, string Error)> Validate()
    {
        var errors = new List<(string, string)>();
        CheckDays(errors, RawSampleDays, nameof(RawSampleDays), "Raw sample retention");
        CheckDays(errors, RollupDays, nameof(RollupDays), "Rollup retention");
        CheckDays(errors, EventDays, nameof(EventDays), "Event retention");
        if (KeepConfigVersions is < 1 or > MaxKeepVersions)
        {
            errors.Add((nameof(KeepConfigVersions), $"Config versions to keep must be between 1 and {MaxKeepVersions}, or empty to keep all."));
        }

        return errors;
    }

    private static void CheckDays(List<(string, string)> errors, int days, string field, string label)
    {
        if (days is < 1 or > MaxDays)
        {
            errors.Add((field, $"{label} must be between 1 and {MaxDays} days."));
        }
    }

    /// <summary>
    /// Raw samples strictly before this are purged: the retention cutoff, but never past the start of the newest
    /// rolled-up hour, so samples are never deleted before they have been rolled up. Null means purge nothing.
    /// </summary>
    public DateTime? RawPurgeCutoff(DateTime nowUtc, DateTime? newestRolledHourUtc)
    {
        if (newestRolledHourUtc is null)
        {
            return null;
        }

        var cutoff = nowUtc.AddDays(-RawSampleDays);
        return cutoff < newestRolledHourUtc.Value ? cutoff : newestRolledHourUtc.Value;
    }
}
