using System.Text;
using System.Text.RegularExpressions;

namespace Yanmt.Core.ConfigBackups;

/// <summary>
/// Turns a raw <c>show running-config</c> capture into the text that is compared and hashed. Volatile
/// lines (timestamps, byte counts, the clock-period drift line) are dropped so an untouched device
/// doesn't produce a new version every day. The stored raw config is never altered; only this
/// derived text feeds change detection.
/// </summary>
public static partial class ConfigNormalizer
{
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Normalizes <paramref name="raw"/>: LF line endings, trailing whitespace trimmed, built-in volatile
    /// lines and any line matching one of <paramref name="extraPatterns"/> removed, leading and trailing
    /// blank lines dropped, one final newline. Patterns are matched anywhere in the line, case-insensitively.
    /// Throws <see cref="ArgumentException"/> for a pattern that doesn't compile and
    /// <see cref="RegexMatchTimeoutException"/> for one that runs too long, so a bad rule fails the
    /// backup visibly instead of silently changing what gets compared.
    /// </summary>
    public static string Normalize(string raw, IEnumerable<string>? extraPatterns = null)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var extras = (extraPatterns ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout))
            .ToList();

        var kept = new List<string>();
        foreach (var rawLine in raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (VolatileLine().IsMatch(line) || extras.Exists(r => r.IsMatch(line)))
            {
                continue;
            }

            kept.Add(line);
        }

        var start = 0;
        var end = kept.Count;
        while (start < end && kept[start].Length == 0)
        {
            start++;
        }

        while (end > start && kept[end - 1].Length == 0)
        {
            end--;
        }

        if (start == end)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (var i = start; i < end; i++)
        {
            sb.Append(kept[i]).Append('\n');
        }

        return sb.ToString();
    }

    [GeneratedRegex(
        @"^(?:Building configuration\.\.\."
        + @"|Current configuration\s*:\s*\d+\s+bytes"
        + @"|Using\s+\d+\s+out\s+of\s+\d+\s+bytes"
        + @"|!\s*Last configuration change\b.*"
        + @"|!\s*NVRAM config last updated\b.*"
        + @"|!\s*No configuration change since last restart\b.*"
        + @"|ntp clock-period\b.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VolatileLine();
}
