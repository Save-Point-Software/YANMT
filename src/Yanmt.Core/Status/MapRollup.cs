namespace Yanmt.Core.Status;

/// <summary>What a map contains, for rollup purposes: the nodes placed on it and the submaps it links to.</summary>
public sealed record MapContents(IReadOnlyCollection<int> NodeIds, IReadOnlyCollection<int> SubmapIds);

/// <summary>
/// Computes the color shown on a submap icon: the worst node color of everything inside it,
/// recursively (red &gt; orange &gt; yellow &gt; green &gt; gray; an empty map is gray). Map graphs are user-built, so cycles (A contains B contains A) are possible;
/// a map already on the current path contributes nothing rather than recursing forever.
/// </summary>
public sealed class MapRollup
{
    private readonly IReadOnlyDictionary<int, MapContents> _maps;
    private readonly Func<int, NodeColor> _nodeState;
    private readonly Dictionary<int, NodeColor> _memo = [];

    /// <param name="maps">Every map, keyed by map id.</param>
    /// <param name="nodeState">Current color of a node (<see cref="NodeColorRollup"/>).</param>
    public MapRollup(IReadOnlyDictionary<int, MapContents> maps, Func<int, NodeColor> nodeState)
    {
        _maps = maps;
        _nodeState = nodeState;
    }

    public NodeColor StateOf(int mapId) => Compute(mapId, []);

    private NodeColor Compute(int mapId, HashSet<int> path)
    {
        if (_memo.TryGetValue(mapId, out var cached))
        {
            return cached;
        }

        if (!_maps.TryGetValue(mapId, out var contents) || !path.Add(mapId))
        {
            return NodeColor.NoData;
        }

        var states = new List<NodeColor>(contents.NodeIds.Count + contents.SubmapIds.Count);
        states.AddRange(contents.NodeIds.Select(_nodeState));
        foreach (var submapId in contents.SubmapIds)
        {
            if (path.Contains(submapId))
            {
                continue; // cycle: ignore the back-edge
            }

            states.Add(Compute(submapId, path));
        }

        path.Remove(mapId);
        var result = NodeColorExtensions.Worst(states);

        // Only memoize results computed with no cycle pruning on the path, so a pruned
        // partial result is never reused from a different entry point.
        if (path.Count == 0)
        {
            _memo[mapId] = result;
        }

        return result;
    }
}
