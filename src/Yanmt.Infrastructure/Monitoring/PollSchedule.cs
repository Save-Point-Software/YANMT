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
/// Next-due times for one poll type. New targets are staggered over the first few seconds so a
/// restart doesn't fire every node at once, and each target keeps a steady cadence afterwards.
/// </summary>
internal sealed class PollSchedule
{
    private static readonly TimeSpan MaxStagger = TimeSpan.FromSeconds(15);

    private readonly Dictionary<int, DateTimeOffset> _due = [];

    public TimeSpan Interval { get; private set; }

    public int Count => _due.Count;

    public void SetTargets(IReadOnlyCollection<int> targets, TimeSpan interval, DateTimeOffset now)
    {
        Interval = interval;
        var keep = targets.ToHashSet();
        foreach (var id in _due.Keys.Where(id => !keep.Contains(id)).ToList())
        {
            _due.Remove(id);
        }

        var added = targets.Where(id => !_due.ContainsKey(id)).ToList();
        var window = interval < MaxStagger ? interval : MaxStagger;
        for (var i = 0; i < added.Count; i++)
        {
            _due[added[i]] = now + window * i / added.Count;
        }

        // A shorter interval takes effect without waiting out the old one.
        foreach (var (id, due) in _due.ToList())
        {
            if (due > now + interval)
            {
                _due[id] = now + interval;
            }
        }
    }

    /// <summary>Targets due now, each rescheduled one interval on (or from now, if it fell behind).</summary>
    public List<int> TakeDue(DateTimeOffset now)
    {
        var due = _due.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList();
        foreach (var id in due)
        {
            var next = _due[id] + Interval;
            _due[id] = next > now ? next : now + Interval;
        }

        return due;
    }
}
