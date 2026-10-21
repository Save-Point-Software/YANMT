namespace Yanmt.Infrastructure.Monitoring;

/// <summary>
/// In-process pub/sub of "these nodes' status changed". Changes are coalesced for a short window
/// so a burst of poll results becomes one notification; Blazor circuits then re-render only what
/// changed. Subscribers are called on a timer thread and must marshal to their own context.
/// </summary>
public sealed class StatusBroadcaster : IDisposable
{
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(250);

    private readonly Lock _lock = new();
    private readonly ITimer _timer;
    private HashSet<int> _pending = [];
    private bool _armed;

    public StatusBroadcaster(TimeProvider clock)
    {
        _timer = clock.CreateTimer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised with the ids of nodes whose state, value, or existence changed.</summary>
    public event Action<IReadOnlyCollection<int>>? Changed;

    public void Publish(IEnumerable<int> nodeIds)
    {
        lock (_lock)
        {
            _pending.UnionWith(nodeIds);
            if (_pending.Count > 0 && !_armed)
            {
                _armed = true;
                _timer.Change(CoalesceWindow, Timeout.InfiniteTimeSpan);
            }
        }
    }

    internal void Flush()
    {
        HashSet<int> batch;
        lock (_lock)
        {
            _armed = false;
            if (_pending.Count == 0)
            {
                return;
            }

            batch = _pending;
            _pending = [];
        }

        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<IReadOnlyCollection<int>>)handler)(batch);
            }
            catch (Exception)
            {
                // A broken subscriber (e.g. a disconnected circuit) must not stop the others.
            }
        }
    }

    public void Dispose() => _timer.Dispose();
}
