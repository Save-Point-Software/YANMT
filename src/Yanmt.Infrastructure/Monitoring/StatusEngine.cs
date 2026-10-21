using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yanmt.Core.Monitoring;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Collectors.Icmp;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Data.Entities;

namespace Yanmt.Infrastructure.Monitoring;

/// <summary>A finished poll, queued for the status engine.</summary>
public abstract record PollResult(int NodeId, DateTime TimestampUtc);

/// <summary>An ICMP burst. Result is null when the poll itself failed (not the same as no reply).</summary>
public sealed record IcmpPollResult(int NodeId, DateTime TimestampUtc, IcmpSettings Settings, IcmpResult? Result, string? Error = null)
    : PollResult(NodeId, TimestampUtc);

/// <summary>
/// The single consumer of poll results. It evaluates each result, updates StatusSource (with
/// dampening), records samples and state-change events, then refreshes the in-memory cache and
/// tells the UI which nodes changed. Results are applied in batches, one transaction each, so the
/// DB sees a few writes per second however many nodes are polled.
/// </summary>
public sealed class StatusEngine(
    IDbContextFactory<YanmtDbContext> dbFactory,
    StatusCache cache,
    StatusBroadcaster broadcaster,
    TimeProvider clock,
    ILogger<StatusEngine> logger)
{
    private const int MaxBatch = 500;

    private readonly Channel<PollResult> _queue = Channel.CreateBounded<PollResult>(
        new BoundedChannelOptions(10_000) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    public ValueTask SubmitAsync(PollResult result, CancellationToken cancellationToken = default) =>
        _queue.Writer.WriteAsync(result, cancellationToken);

    /// <summary>Drains the queue until cancelled. A failing batch is logged and dropped; the loop carries on.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var reader = _queue.Reader;
        var batch = new List<PollResult>(MaxBatch);
        while (await reader.WaitToReadAsync(cancellationToken))
        {
            batch.Clear();
            while (batch.Count < MaxBatch && reader.TryRead(out var item))
            {
                batch.Add(item);
            }

            try
            {
                await ApplyAsync(batch, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Failed to apply {Count} poll result(s); they were dropped", batch.Count);
            }
        }
    }

    /// <summary>
    /// Makes StatusSource rows match the inventory: a node that is disabled, or has ICMP turned
    /// off, gets its ICMP source disabled (Unknown, out of the rollup); turning it back on
    /// re-enables it as Unknown until the next poll. Then reloads the cache for every node.
    /// </summary>
    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var nodes = await db.Nodes.AsNoTracking()
            .Select(n => new { n.Id, Icmp = n.Enabled && n.IcmpEnabled })
            .ToDictionaryAsync(n => n.Id, n => n.Icmp, cancellationToken);
        var sources = await db.StatusSources.Where(s => s.SourceType == StatusSourceType.Icmp).ToListAsync(cancellationToken);

        foreach (var source in sources)
        {
            var wanted = nodes.GetValueOrDefault(source.NodeId);
            if (source.Enabled == wanted)
            {
                continue;
            }

            source.Enabled = wanted;
            source.State = Severity.Unknown;
            source.PendingState = null;
            source.PendingCount = 0;
            source.Value = null;
            source.ValueText = null;
            source.SinceUtc = now;
            source.LastUpdateUtc = null; // the first poll after re-enabling isn't a "state change"
        }

        await db.SaveChangesAsync(cancellationToken);
        broadcaster.Publish(await cache.RefreshAsync(db, null, cancellationToken));
    }

    internal async Task ApplyAsync(IReadOnlyList<PollResult> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var nodeIds = batch.Select(r => r.NodeId).Distinct().ToList();
        var nodes = await db.Nodes.AsNoTracking()
            .Where(n => nodeIds.Contains(n.Id))
            .Select(n => new { n.Id, n.Hostname, n.Enabled, n.IcmpEnabled })
            .ToDictionaryAsync(n => n.Id, cancellationToken);
        var sources = await db.StatusSources
            .Where(s => nodeIds.Contains(s.NodeId) && s.SourceType == StatusSourceType.Icmp && s.SourceKey == "")
            .ToDictionaryAsync(s => s.NodeId, cancellationToken);

        foreach (var result in batch.OfType<IcmpPollResult>().OrderBy(r => r.TimestampUtc))
        {
            // The node may have been deleted or switched off while the poll was in flight.
            if (!nodes.TryGetValue(result.NodeId, out var node) || !node.Enabled || !node.IcmpEnabled)
            {
                continue;
            }

            if (!sources.TryGetValue(result.NodeId, out var source))
            {
                source = new StatusSource
                {
                    NodeId = result.NodeId,
                    SourceType = StatusSourceType.Icmp,
                    SinceUtc = result.TimestampUtc,
                };
                db.StatusSources.Add(source);
                sources[result.NodeId] = source;
            }

            ApplyIcmp(db, source, node.Hostname, result);
        }

        await db.SaveChangesAsync(cancellationToken);
        broadcaster.Publish(await cache.RefreshAsync(db, nodeIds, cancellationToken));
    }

    private static void ApplyIcmp(YanmtDbContext db, StatusSource source, string hostname, IcmpPollResult result)
    {
        var previous = new DampedState(source.State, source.PendingState, source.PendingCount);
        DampedState next;
        string reason;
        if (result.Result is not { } r)
        {
            // The poll itself failed (not "no reply"): we have no data, so the state is Unknown.
            next = DampedState.Unknown;
            source.Value = null;
            source.ValueText = "poll failed";
            reason = $"poll failed: {result.Error}";
        }
        else
        {
            db.IcmpSamples.Add(new IcmpSample
            {
                NodeId = result.NodeId,
                TimestampUtc = result.TimestampUtc,
                Sent = r.Sent,
                Received = r.Received,
                MinMs = r.MinMs,
                AvgMs = r.AvgMs,
                MaxMs = r.MaxMs,
                LossPercent = r.LossPercent,
            });

            var evaluation = IcmpEvaluator.Evaluate(previous, new IcmpMeasurement(r.Sent, r.Received, r.AvgMs), result.Settings);
            next = evaluation.Next;
            source.Value = evaluation.Value;
            source.ValueText = evaluation.ValueText;
            reason = evaluation.Reason;
        }

        if (next.State != source.State)
        {
            // The first data a source ever gets (or gets after being re-enabled) isn't a change.
            if (source.LastUpdateUtc is not null || source.State != Severity.Unknown)
            {
                db.Events.Add(new Event
                {
                    TimestampUtc = result.TimestampUtc,
                    NodeId = result.NodeId,
                    Source = StatusSourceType.Icmp,
                    OldState = source.State,
                    NewState = next.State,
                    Message = Truncate($"{hostname}: ICMP {source.State} → {next.State}: {reason}", 1000),
                });
            }

            source.SinceUtc = result.TimestampUtc;
        }

        source.State = next.State;
        source.PendingState = next.PendingState;
        source.PendingCount = next.PendingCount;
        source.Enabled = true;
        source.LastUpdateUtc = result.TimestampUtc;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
