using Yanmt.Infrastructure.Events;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Yanmt.Core.Inventory;
using Yanmt.Infrastructure.Collectors.Icmp;
using Yanmt.Infrastructure.Data.Entities;
using Yanmt.Infrastructure.Monitoring;

namespace Yanmt.Infrastructure.Tests.Monitoring;

internal static class MonitoringTestSupport
{
    public static async Task<int> AddNodeAsync(this TestDatabase test, string hostname, string ip, bool enabled = true, bool icmp = true)
    {
        await using var db = test.Open();
        var now = test.Clock.GetUtcNow().UtcDateTime;
        var node = new Node
        {
            Hostname = hostname,
            ManagementIp = ip,
            Icon = NodeIconType.Router,
            Enabled = enabled,
            IcmpEnabled = icmp,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        db.Nodes.Add(node);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return node.Id;
    }

    public static (StatusEngine Engine, StatusCache Cache, StatusBroadcaster Broadcaster) Engine(this TestDatabase test, INotifier? notifier = null)
    {
        var cache = new StatusCache();
        var broadcaster = new StatusBroadcaster(test.Clock);
        return (new StatusEngine(test.Factory, cache, broadcaster, notifier ?? new InAppNotifier(), test.Clock, NullLogger<StatusEngine>.Instance), cache, broadcaster);
    }

    public static IcmpResult Replies(double avgMs, int received = 4) => new(4, received, avgMs, avgMs, avgMs, null);

    public static readonly IcmpResult NoReply = new(4, 0, null, null, null, System.Net.NetworkInformation.IPStatus.TimedOut);
}

/// <summary>An ICMP probe that answers from a script, records calls, and can be held open to test overlap.</summary>
internal sealed class FakeIcmpProbe : IIcmpProbe
{
    private readonly Lock _lock = new();

    public List<IPAddress> Calls { get; } = [];

    public Func<IPAddress, IcmpResult> Answer { get; set; } = _ => MonitoringTestSupport.Replies(2);

    /// <summary>When set, every probe waits for this before answering.</summary>
    public TaskCompletionSource? Hold { get; set; }

    public async Task<IcmpResult> ProbeAsync(IPAddress address, int count, TimeSpan timeout, TimeSpan spacing, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            Calls.Add(address);
        }

        if (Hold is { } hold)
        {
            await hold.Task.WaitAsync(cancellationToken);
        }

        return Answer(address);
    }

    public int CallCount
    {
        get
        {
            lock (_lock)
            {
                return Calls.Count;
            }
        }
    }
}
