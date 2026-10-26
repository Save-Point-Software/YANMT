namespace Yanmt.Infrastructure.Monitoring;

/// <summary>Bootstrap polling limits, bound from "Yanmt:Polling" in appsettings.json.</summary>
public sealed class PollingOptions
{
    public const string SectionName = "Yanmt:Polling";

    /// <summary>ICMP bursts in flight at once.</summary>
    public int IcmpConcurrency { get; set; } = 32;

    /// <summary>SNMP default-stats polls in flight at once.</summary>
    public int SnmpConcurrency { get; set; } = 16;

    /// <summary>Reload targets at least this often, even without a change signal.</summary>
    public int ReloadSeconds { get; set; } = 300;
}

/// <summary>
/// Bumped whenever nodes or polling settings change, so pollers reload their targets on the next
/// tick instead of waiting for the periodic reload.
/// </summary>
public sealed class InventoryChangeSignal
{
    private long _version;

    public long Version => Interlocked.Read(ref _version);

    public void Notify() => Interlocked.Increment(ref _version);
}

/// <summary>
/// Next-due times for one poller. New targets are staggered over the first few seconds so a
/// restart doesn't fire every node at once, and each target keeps a steady cadence at its own
/// interval afterwards.
/// </summary>
internal sealed class PollSchedule
{
    private static readonly TimeSpan MaxStagger = TimeSpan.FromSeconds(15);

    private readonly Dictionary<int, (DateTimeOffset Due, TimeSpan Interval)> _due = [];

    public int Count => _due.Count;

    public void SetTargets(IReadOnlyCollection<int> targets, TimeSpan interval, DateTimeOffset now) =>
        SetTargets(targets.ToDictionary(t => t, _ => interval), now);

    public void SetTargets(IReadOnlyDictionary<int, TimeSpan> targets, DateTimeOffset now)
    {
        foreach (var id in _due.Keys.Where(id => !targets.ContainsKey(id)).ToList())
        {
            _due.Remove(id);
        }

        var added = targets.Keys.Where(id => !_due.ContainsKey(id)).ToList();
        for (var i = 0; i < added.Count; i++)
        {
            var interval = targets[added[i]];
            var window = interval < MaxStagger ? interval : MaxStagger;
            _due[added[i]] = (now + window * i / added.Count, interval);
        }

        // A changed interval applies now; a shorter one doesn't wait out the old one.
        foreach (var (id, (due, _)) in _due.ToList())
        {
            var interval = targets[id];
            _due[id] = (due > now + interval ? now + interval : due, interval);
        }
    }

    /// <summary>Targets due now, each rescheduled one interval on (or from now, if it fell behind).</summary>
    public List<int> TakeDue(DateTimeOffset now)
    {
        var due = _due.Where(kv => kv.Value.Due <= now).Select(kv => kv.Key).ToList();
        foreach (var id in due)
        {
            var (at, interval) = _due[id];
            var next = at + interval;
            _due[id] = (next > now ? next : now + interval, interval);
        }

        return due;
    }
}
