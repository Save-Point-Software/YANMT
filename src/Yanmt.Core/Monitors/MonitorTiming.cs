using System.Globalization;

namespace Yanmt.Core.Monitors;

/// <summary>How long a monitor takes to show a change, given its interval and dampening.</summary>
public static class MonitorTiming
{
    /// <summary>
    /// Worst-case time from a change on the device to the new state taking effect: the first poll
    /// that sees it can be up to one interval later, then it must match on each of the remaining polls.
    /// </summary>
    public static TimeSpan WorstCaseDetection(int intervalSeconds, int consecutiveMatches) =>
        TimeSpan.FromSeconds((long)Math.Max(intervalSeconds, 0) * Math.Max(consecutiveMatches, 1));

    /// <summary>E.g. "up to 2 min (2 × 60 s)" or "up to 5 min (1 × 300 s)".</summary>
    public static string DescribeDetection(int intervalSeconds, int consecutiveMatches)
    {
        var matches = Math.Max(consecutiveMatches, 1);
        var total = WorstCaseDetection(intervalSeconds, matches);
        return $"up to {Duration(total)} ({matches} × {intervalSeconds.ToString(CultureInfo.InvariantCulture)} s)";
    }

    /// <summary>"45 s", "2 min", "2 min 30 s", "1 h 5 min".</summary>
    public static string Duration(TimeSpan t)
    {
        var inv = CultureInfo.InvariantCulture;
        if (t.TotalSeconds < 60)
        {
            return ((int)t.TotalSeconds).ToString(inv) + " s";
        }

        if (t.TotalHours < 1)
        {
            return t.Seconds == 0 ? $"{t.Minutes.ToString(inv)} min" : $"{t.Minutes.ToString(inv)} min {t.Seconds.ToString(inv)} s";
        }

        var hours = ((int)t.TotalHours).ToString(inv);
        return t.Minutes == 0 ? $"{hours} h" : $"{hours} h {t.Minutes.ToString(inv)} min";
    }
}
