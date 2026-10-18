using System.Net;
using System.Net.NetworkInformation;

namespace Yanmt.Infrastructure.Collectors.Icmp;

/// <summary>Result of a burst of echoes. Latencies are null when nothing came back.</summary>
public sealed record IcmpResult(int Sent, int Received, double? MinMs, double? AvgMs, double? MaxMs, IPStatus? LastFailure)
{
    public double LossPercent => Sent == 0 ? 100 : 100.0 * (Sent - Received) / Sent;
}

/// <summary>ICMP echo via the built-in <see cref="Ping"/> (no admin rights needed).</summary>
public sealed class IcmpProbe
{
    private static readonly byte[] Payload = new byte[32];

    public async Task<IcmpResult> ProbeAsync(IPAddress address, int count, TimeSpan timeout, TimeSpan spacing, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        using var ping = new Ping();
        var rtts = new List<long>(count);
        IPStatus? lastFailure = null;

        for (var i = 0; i < count; i++)
        {
            if (i > 0 && spacing > TimeSpan.Zero)
            {
                await Task.Delay(spacing, cancellationToken);
            }

            try
            {
                var reply = await ping.SendPingAsync(address, timeout, Payload, new PingOptions(ttl: 64, dontFragment: false), cancellationToken);
                if (reply.Status == IPStatus.Success)
                {
                    rtts.Add(reply.RoundtripTime);
                }
                else
                {
                    lastFailure = reply.Status;
                }
            }
            catch (PingException)
            {
                // Raised for local failures (e.g. no route on this host); counts as a lost echo.
                lastFailure = IPStatus.Unknown;
            }
        }

        return rtts.Count == 0
            ? new IcmpResult(count, 0, null, null, null, lastFailure)
            : new IcmpResult(count, rtts.Count, rtts.Min(), rtts.Average(), rtts.Max(), lastFailure);
    }
}
