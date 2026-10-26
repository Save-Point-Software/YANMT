using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Data.Entities;

namespace Yanmt.Infrastructure.Monitoring;

/// <summary>Current state and value of one enabled status source.</summary>
public sealed record SourceStatus(StatusSourceType Type, string Key, Severity State, double? Value, string? ValueText, DateTime SinceUtc, string? DisplayName = null);

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

        var changed = new List<int>();
        foreach (var node in nodes)
        {
            var list = sources[node.Id]
                .OrderBy(s => s.SourceType).ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.SourceKey, StringComparer.Ordinal)
                .Select(s => new SourceStatus(s.SourceType, s.SourceKey, s.State, s.Value, s.ValueText, s.SinceUtc, s.DisplayName))
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
