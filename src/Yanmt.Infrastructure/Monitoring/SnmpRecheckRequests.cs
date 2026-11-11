namespace Yanmt.Infrastructure.Monitoring;

/// <summary>
/// Nodes whose SNMP poll should run now, out of cycle. <see cref="StatusEngine"/> asks for one when
/// a node's ICMP goes Critical, so red (down) vs. yellow (ping blocked) is settled in seconds
/// instead of waiting up to a full SNMP interval. <see cref="SnmpPoller"/> picks requests up on its
/// next tick; a node already being polled keeps its request until that poll finishes.
/// </summary>
public sealed class SnmpRecheckRequests
{
    private readonly Lock _lock = new();
    private readonly HashSet<int> _pending = [];

    public void Request(IEnumerable<int> nodeIds)
    {
        lock (_lock)
        {
            _pending.UnionWith(nodeIds);
        }
    }

    internal IReadOnlyList<int> Pending()
    {
        lock (_lock)
        {
            return [.. _pending];
        }
    }

    internal void Done(int nodeId)
    {
        lock (_lock)
        {
            _pending.Remove(nodeId);
        }
    }
}
