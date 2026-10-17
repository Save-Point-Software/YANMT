namespace Yanmt.Core.Status;

/// <summary>
/// The single severity scale shared by every status source (ICMP, default SNMP stats,
/// custom monitors, backups). Persisted by name, so the numeric values are not significant.
/// </summary>
public enum Severity
{
    Normal = 0,
    Warning = 1,
    Critical = 2,

    /// <summary>No data yet, or the poll failed.</summary>
    Unknown = 3,
}

public static class SeverityExtensions
{
    /// <summary>
    /// Rank used for "worst state wins" rollups: Critical &gt; Warning &gt; Unknown &gt; Normal.
    /// Unknown outranks Normal so a node whose SNMP polling is failing never shows green,
    /// but a real Warning/Critical from another source still wins over missing data.
    /// </summary>
    public static int Rank(this Severity severity) => severity switch
    {
        Severity.Normal => 0,
        Severity.Unknown => 1,
        Severity.Warning => 2,
        Severity.Critical => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, null),
    };

    public static Severity WorseOf(this Severity a, Severity b) => a.Rank() >= b.Rank() ? a : b;
}
