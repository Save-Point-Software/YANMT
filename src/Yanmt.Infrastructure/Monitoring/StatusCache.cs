using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Data.Entities;

namespace Yanmt.Infrastructure.Monitoring;

/// <summary>
/// Current state and value of one enabled status source. For custom monitors, the pending fields
/// carry a dampened change that hasn't taken effect yet (<see cref="RequiredMatches"/> is the
/// definition's consecutive matches); they are null/0 for built-in sources.
/// </summary>
public sealed record SourceStatus(
    StatusSourceType Type, string Key, Severity State, double? Value, string? ValueText, DateTime SinceUtc, string? DisplayName = null,
    Severity? PendingState = null, int PendingCount = 0, int? RequiredMatches = null)
{
    /// <summary>E.g. "Critical pending (1 of 2 matching polls before Critical)", or null.</summary>
    public string? PendingText => RequiredMatches is { } required
        ? Yanmt.Core.Monitors.PendingText.For(State, PendingState, PendingCount, required)
        : null;

    /// <summary>The pending state when <see cref="PendingText"/> has something to show.</summary>
    public Severity? ShownPending => PendingText is null ? null : PendingState;
}

/// <summary>A node's rolled-up state: worst of its enabled sources; Unknown if the node is disabled or has none.</summary>
public sealed record NodeStatus(int NodeId, bool Enabled, Severity State, IReadOnlyList<SourceStatus> Sources)
{
    public SourceStatus? Source(StatusSourceType type, string key = "") =>
        Sources.FirstOrDefault(s => s.Type == type && s.Key == key);
}

/// <summary>
/// In-memory copy of the StatusSource table, so the map and lists read live state without a
/// query per render. Only <see cref="StatusEngine"/> writes it, always after the DB commit.
/// </summary>
public sealed class StatusCache
{
    private readonly ConcurrentDictionary<int, NodeStatus> _nodes = new();

    public NodeStatus? Get(int nodeId) => _nodes.GetValueOrDefault(nodeId);

    public Severity StateOf(int nodeId) => _nodes.TryGetValue(nodeId, out var n) ? n.State : Severity.Unknown;

    /// <summary>Reloads these nodes (all nodes when null) from the DB. Returns the nodes whose entry changed.</summary>
    internal async Task<List<int>> RefreshAsync(YanmtDbContext db, IReadOnlyCollection<int>? nodeIds, CancellationToken cancellationToken)
    {
        var nodeQuery = db.Nodes.AsNoTracking();
        var sourceQuery = db.StatusSources.AsNoTracking().Where(s => s.Enabled);
        if (nodeIds is not null)
        {
            nodeQuery = nodeQuery.Where(n => nodeIds.Contains(n.Id));
            sourceQuery = sourceQuery.Where(s => nodeIds.Contains(s.NodeId));
        }

        var nodes = await nodeQuery.Select(n => new { n.Id, n.Enabled }).ToListAsync(cancellationToken);
        var sources = (await sourceQuery.ToListAsync(cancellationToken)).ToLookup(s => s.NodeId);

        // Custom sources are keyed by instance id; their dampening comes from the definition.
        var instanceQuery = db.MonitorInstances.AsNoTracking();
        if (nodeIds is not null)
        {
            instanceQuery = instanceQuery.Where(i => nodeIds.Contains(i.NodeId));
        }

        var required = await instanceQuery
            .Select(i => new { i.Id, i.Definition.ConsecutiveMatches })
            .ToDictionaryAsync(i => i.Id.ToString(CultureInfo.InvariantCulture), i => i.ConsecutiveMatches, cancellationToken);

        var changed = new List<int>();
        foreach (var node in nodes)
        {
            var list = sources[node.Id]
                .OrderBy(s => s.SourceType).ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.SourceKey, StringComparer.Ordinal)
                .Select(s => s.SourceType == StatusSourceType.Custom
                    ? new SourceStatus(s.SourceType, s.SourceKey, s.State, s.Value, s.ValueText, s.SinceUtc, s.DisplayName,
                        s.PendingState, s.PendingCount, required.TryGetValue(s.SourceKey, out var r) ? r : null)
                    : new SourceStatus(s.SourceType, s.SourceKey, s.State, s.Value, s.ValueText, s.SinceUtc, s.DisplayName))
                .ToList();
            var state = node.Enabled ? StatusRollup.Worst(list.Select(s => s.State)) : Severity.Unknown;
            var status = new NodeStatus(node.Id, node.Enabled, state, list);
            if (!_nodes.TryGetValue(node.Id, out var old) || !Same(old, status))
            {
                _nodes[node.Id] = status;
                changed.Add(node.Id);
            }
        }

        // Nodes that were asked about (or cached) but no longer exist were deleted.
        var present = nodes.Select(n => n.Id).ToHashSet();
        foreach (var id in nodeIds ?? _nodes.Keys.ToList())
        {
            if (!present.Contains(id) && _nodes.TryRemove(id, out _))
            {
                changed.Add(id);
            }
        }

        return changed;
    }

    private static bool Same(NodeStatus a, NodeStatus b) =>
        a.Enabled == b.Enabled && a.State == b.State && a.Sources.SequenceEqual(b.Sources);
}
