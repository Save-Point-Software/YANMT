using System.Security.Claims;
using Yanmt.Core.Monitoring;
using Yanmt.Infrastructure.Monitors;
using Yanmt.Infrastructure.Security;

namespace Yanmt.Infrastructure.Monitoring;

/// <summary>What one source did in a manual poll. <see cref="Polled"/> is false when the node has no such source.</summary>
public sealed record ManualPollSource(string Name, bool Polled, bool Ok, string Detail);

/// <summary>A refused (rate-limited) poll has <see cref="RetryAfter"/> set and no sources.</summary>
public sealed record ManualPollOutcome(TimeSpan? RetryAfter, IReadOnlyList<ManualPollSource> Sources)
{
    public bool RateLimited => RetryAfter is not null;
}

/// <summary>
/// "Poll now": runs a node's ICMP, default SNMP stats and custom-monitor polls immediately and
/// submits the results to the <see cref="StatusEngine"/> like scheduled polls, so dampening and
/// recovery behave identically (a success clears failed/Unknown/reboot-Warning at once). Admin
/// only, enforced here rather than in the UI, and limited to one poll per node per
/// <see cref="MinInterval"/>. The regular schedules are not touched.
/// </summary>
public sealed class ManualPollService(
    IcmpPoller icmp,
    SnmpPoller snmp,
    MonitorPoller monitors,
    StatusEngine engine,
    TimeProvider clock)
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(20);

    private readonly Lock _lock = new();
    private readonly Dictionary<int, DateTimeOffset> _last = [];

    /// <exception cref="UnauthorizedAccessException">The caller is not an admin.</exception>
    public async Task<ManualPollOutcome> PollNowAsync(int nodeId, ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        if (!principal.IsInRole(YanmtRoles.Admin))
        {
            throw new UnauthorizedAccessException("Only admins can poll a node manually.");
        }

        if (TryReserve(nodeId) is { } wait)
        {
            return new ManualPollOutcome(wait, []);
        }

        var all = await Task.WhenAll(
            icmp.PollNowAsync(nodeId, cancellationToken),
            snmp.PollNowAsync(nodeId, cancellationToken),
            monitors.PollNowAsync(nodeId, cancellationToken));

        foreach (var result in all.SelectMany(r => r))
        {
            await engine.SubmitAsync(result, cancellationToken);
        }

        return new ManualPollOutcome(null,
        [
            Summarize("ICMP", all[0], r => r is IcmpPollResult i ? Icmp(i) : null),
            Summarize("Default SNMP stats", all[1], r => r is SnmpPollResult s ? Snmp(s) : null),
            Summarize("Custom monitors", all[2], r => r is MonitorPollResult m ? Monitors(m) : null),
        ]);
    }

    /// <summary>Claims the node's slot, or returns how long until it frees up.</summary>
    private TimeSpan? TryReserve(int nodeId)
    {
        var now = clock.GetUtcNow();
        lock (_lock)
        {
            if (_last.TryGetValue(nodeId, out var last) && now - last < MinInterval)
            {
                return MinInterval - (now - last);
            }

            _last[nodeId] = now;
            return null;
        }
    }

    private static ManualPollSource Summarize(string name, IReadOnlyList<PollResult> results, Func<PollResult, (bool Ok, string Detail)?> describe)
    {
        if (results.Count == 0)
        {
            return new ManualPollSource(name, false, false, "Not monitored on this node.");
        }

        var parts = results.Select(describe).OfType<(bool Ok, string Detail)>().ToList();
        return new ManualPollSource(name, true, parts.All(p => p.Ok), string.Join("; ", parts.Select(p => p.Detail)));
    }

    private static (bool, string) Icmp(IcmpPollResult r) => r.Result is { } x
        ? (x.Received > 0, x.Received > 0 ? $"{x.Received} of {x.Sent} replies, average {x.AvgMs:0.#} ms" : $"no reply to {x.Sent} echoes")
        : (false, $"poll failed: {r.Error}");

    private static (bool, string) Snmp(SnmpPollResult r) => r.Reading is not null
        ? (true, "Read system info, uptime, CPU and memory.")
        : (false, $"poll failed ({r.Failure}): {r.Error}");

    private static (bool, string) Monitors(MonitorPollResult r) => r.Reading is { } x
        ? (true, $"{r.Works.Count} monitor(s), {x.Works.Sum(w => w.Rows.Count)} value(s) read")
        : (false, $"poll failed ({r.Failure}): {r.Error}");
}
