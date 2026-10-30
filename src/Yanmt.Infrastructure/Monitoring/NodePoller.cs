using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Yanmt.Infrastructure.Monitoring;

/// <summary>
/// Shared machinery for a per-node poller: a 1-second tick hands due nodes to a bounded set of
/// workers; a node already in flight is never started twice, so a slow or dead node only ever
/// occupies one worker. Targets and settings are reloaded when <see cref="InventoryChangeSignal"/>
/// changes and periodically. Every result goes to the <see cref="StatusEngine"/>; a poll that
/// throws becomes a failure result, and nothing a poll does can stop the loop.
/// </summary>
public abstract class NodePoller<TTarget, TSettings>(
    IServiceScopeFactory scopes,
    StatusEngine engine,
    InventoryChangeSignal signal,
    int concurrency,
    int reloadSeconds,
    TimeProvider clock,
    ILogger logger) : BackgroundService
    where TSettings : notnull
{
    private readonly PollSchedule _schedule = new();
    private readonly ConcurrentDictionary<int, byte> _inFlight = new();
    private readonly SemaphoreSlim _workers = new(Math.Max(1, concurrency));
    private IReadOnlyDictionary<int, TTarget> _targets = new Dictionary<int, TTarget>();
    private TSettings? _settings;
    private long _loadedVersion = -1;
    private DateTimeOffset _nextReload = DateTimeOffset.MinValue;

    /// <summary>For log messages, e.g. "ICMP".</summary>
    protected abstract string Kind { get; }

    /// <summary>
    /// Reads settings and the current targets, keyed by a stable id (the node id for one-job-per-node
    /// pollers). <paramref name="services"/> is a fresh scope.
    /// </summary>
    protected abstract Task<(TSettings Settings, TimeSpan Interval, IReadOnlyDictionary<int, TTarget> Targets)> LoadAsync(
        IServiceProvider services, CancellationToken cancellationToken);

    /// <summary>A target's own interval, when targets don't all share the poller's.</summary>
    protected virtual TimeSpan IntervalOf(TTarget target, TimeSpan interval) => interval;

    /// <summary>Polls one target. Exceptions are turned into a result by <see cref="Failed"/>.</summary>
    protected abstract Task<PollResult> PollAsync(int key, TTarget target, TSettings settings, DateTime startedUtc, CancellationToken cancellationToken);

    protected abstract PollResult Failed(int key, TTarget target, TSettings settings, DateTime startedUtc, Exception exception);

    /// <summary>Longest a single poll may run before it is cancelled and reported as failed.</summary>
    protected abstract TimeSpan Backstop(TSettings settings);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "{Kind} poller tick failed; retrying on the next tick", Kind);
                _nextReload = DateTimeOffset.MinValue; // reload from scratch next time
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Polls started and not yet handed to the engine. Tests use it to wait for a quiet poller.</summary>
    internal int InFlightCount => _inFlight.Count;

    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var version = signal.Version;
        if (version != _loadedVersion || now >= _nextReload)
        {
            using var scope = scopes.CreateScope();
            var (settings, interval, targets) = await LoadAsync(scope.ServiceProvider, cancellationToken);
            _settings = settings;
            _targets = targets;
            _schedule.SetTargets(targets.ToDictionary(t => t.Key, t => IntervalOf(t.Value, interval)), now);
            await engine.SyncAsync(cancellationToken);
            _loadedVersion = version;
            _nextReload = now + TimeSpan.FromSeconds(Math.Max(10, reloadSeconds));
            logger.LogInformation("{Kind} polling {Count} target(s), default interval {Interval} s", Kind, targets.Count, interval.TotalSeconds);
        }

        foreach (var key in _schedule.TakeDue(now))
        {
            if (_targets.TryGetValue(key, out var target) && _inFlight.TryAdd(key, 0))
            {
                _ = RunAsync(key, target, _settings!, cancellationToken);
            }
        }
    }

    private async Task RunAsync(int key, TTarget target, TSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            PollResult result;
            await _workers.WaitAsync(cancellationToken);
            try
            {
                var started = clock.GetUtcNow().UtcDateTime;
                using var guard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                guard.CancelAfter(Backstop(settings));
                try
                {
                    result = await PollAsync(key, target, settings, started, guard.Token);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogDebug(ex, "{Kind} poll {Key} failed", Kind, key);
                    result = Failed(key, target, settings, started, ex);
                }
            }
            finally
            {
                _workers.Release();
            }

            await engine.SubmitAsync(result, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Kind} poll {Key} could not be queued", Kind, key);
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    public override void Dispose()
    {
        _workers.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
