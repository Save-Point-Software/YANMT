namespace Yanmt.Core.Status;

/// <summary>What a map contains, for rollup purposes: the nodes placed on it and the submaps it links to.</summary>
public sealed record MapContents(IReadOnlyCollection<int> NodeIds, IReadOnlyCollection<int> SubmapIds);

/// <summary>
/// Computes the state shown on a submap icon: the worst state of everything inside it,
/// recursively. Map graphs are user-built, so cycles (A contains B contains A) are possible;
/// a map already on the current path contributes nothing rather than recursing forever.
/// </summary>
public sealed class MapRollup
{
    private readonly IReadOnlyDictionary<int, MapContents> _maps;
    private readonly Func<int, Severity> _nodeState;
    private readonly Dictionary<int, Severity> _memo = [];

    /// <param name="maps">Every map, keyed by map id.</param>
    /// <param name="nodeState">Current rolled-up state of a node (worst of its enabled monitors).</param>
    public MapRollup(IReadOnlyDictionary<int, MapContents> maps, Func<int, Severity> nodeState)
    {
        _maps = maps;
        _nodeState = nodeState;
    }

    public Severity StateOf(int mapId) => Compute(mapId, []);

    private Severity Compute(int mapId, HashSet<int> path)
    {
        if (_memo.TryGetValue(mapId, out var cached))
        {
            return cached;
        }

        if (!_maps.TryGetValue(mapId, out var contents) || !path.Add(mapId))
        {
            return Severity.Unknown;
        }

        var states = new List<Severity>(contents.NodeIds.Count + contents.SubmapIds.Count);
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
        var result = StatusRollup.Worst(states);

        // Only memoize results computed with no cycle pruning on the path, so a pruned
        // partial result is never reused from a different entry point.
        if (path.Count == 0)
        {
            _memo[mapId] = result;
        }

        return result;
    }
}
