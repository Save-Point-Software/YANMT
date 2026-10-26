using System.Text.RegularExpressions;

namespace Yanmt.Core.ConfigBackups;

/// <summary>Config backup settings (global in v1, admin-editable, stored in the DB by a later change).</summary>
public sealed record ConfigBackupSettings
{
    public const int MaxPatterns = 50;
    public const int MaxPatternLength = 200;

    public bool Enabled { get; init; } = true;

    /// <summary>Gap between scheduled backups. Default daily.</summary>
    public int IntervalSeconds { get; init; } = 86400;

    /// <summary>Extra regexes: any config line matching one is ignored when comparing versions.</summary>
    public IReadOnlyList<string> NormalizationPatterns { get; init; } = [];

    public IReadOnlyList<(string Field, string Error)> Validate()
    {
        var errors = new List<(string, string)>();
        if (IntervalSeconds is < 300 or > 604800)
        {
            errors.Add((nameof(IntervalSeconds), "Interval (seconds) must be between 300 and 604800."));
        }

        if (NormalizationPatterns.Count > MaxPatterns)
        {
            errors.Add((nameof(NormalizationPatterns), $"At most {MaxPatterns} ignore patterns."));
        }

        foreach (var p in NormalizationPatterns)
        {
            if (string.IsNullOrWhiteSpace(p) || p.Length > MaxPatternLength || !Compiles(p))
            {
                errors.Add((nameof(NormalizationPatterns), $"'{p}' isn't a valid regular expression (at most {MaxPatternLength} characters)."));
            }
        }

        return errors;
    }

    private static bool Compiles(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, ConfigNormalizer.RegexTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
