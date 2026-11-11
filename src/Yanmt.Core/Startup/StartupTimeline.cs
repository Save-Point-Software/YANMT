using System.Globalization;

namespace Yanmt.Core.Startup;

/// <summary>
/// Startup phases and when each one ended, measured from process start. Used to find where a slow
/// first start (profile creation, single-file extraction, certificate, migrations) spends its time.
/// </summary>
public sealed class StartupTimeline
{
    private readonly List<(string Phase, TimeSpan EndedAt)> _marks = [];
    private readonly Lock _lock = new();

    /// <summary>Records that <paramref name="phase"/> ended <paramref name="sinceProcessStart"/> after process start.</summary>
    /// <returns>How long the phase took (since the previous mark, or since process start).</returns>
    public TimeSpan Mark(string phase, TimeSpan sinceProcessStart)
    {
        lock (_lock)
        {
            var previous = _marks.Count == 0 ? TimeSpan.Zero : _marks[^1].EndedAt;
            _marks.Add((phase, sinceProcessStart));
            return sinceProcessStart - previous < TimeSpan.Zero ? TimeSpan.Zero : sinceProcessStart - previous;
        }
    }

    public TimeSpan Total
    {
        get
        {
            lock (_lock)
            {
                return _marks.Count == 0 ? TimeSpan.Zero : _marks[^1].EndedAt;
            }
        }
    }

    /// <summary>One line: total, then each phase's duration in order, e.g. "4.2 s total: runtime 0.6 s, config 0.1 s, ...".</summary>
    public string Summary()
    {
        lock (_lock)
        {
            var parts = new List<string>(_marks.Count);
            var previous = TimeSpan.Zero;
            foreach (var (phase, endedAt) in _marks)
            {
                parts.Add($"{phase} {Seconds(endedAt - previous)}");
                previous = endedAt;
            }

            return $"{Seconds(previous)} total: {string.Join(", ", parts)}";
        }
    }

    public static string Seconds(TimeSpan span) =>
        (span < TimeSpan.Zero ? TimeSpan.Zero : span).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
}
