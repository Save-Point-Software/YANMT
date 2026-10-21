using System.Globalization;
using Yanmt.Core.Status;

namespace Yanmt.Core.Monitoring;

/// <summary>One ICMP burst: echoes sent and answered, and the average round trip of the answers.</summary>
public readonly record struct IcmpMeasurement(int Sent, int Received, double? AvgMs)
{
    public double LossPercent => Sent == 0 ? 100 : 100.0 * (Sent - Received) / Sent;
}

/// <summary>A source's state plus the dampening counter carried between polls.</summary>
public readonly record struct DampedState(Severity State, Severity? PendingState, int PendingCount)
{
    public static DampedState Unknown => new(Severity.Unknown, null, 0);
}

/// <summary>The outcome of evaluating one poll: the new state, the label value, and why.</summary>
public sealed record IcmpEvaluation(DampedState Next, double? Value, string ValueText, string Reason);

/// <summary>
/// Turns an ICMP burst into a state. Loss or latency over threshold is Warning straight away. No
/// reply at all is Critical only after <see cref="IcmpSettings.FailuresBeforeCritical"/> consecutive
/// polls; until then the node shows Warning, so a single lost burst never flaps it to red but a
/// down node never sits green either. Any answered poll clears the failure count.
/// </summary>
public static class IcmpEvaluator
{
    public static IcmpEvaluation Evaluate(DampedState previous, IcmpMeasurement m, IcmpSettings settings)
    {
        if (m.Received == 0)
        {
            var failures = previous.State == Severity.Critical
                ? settings.FailuresBeforeCritical
                : previous.PendingState == Severity.Critical ? previous.PendingCount + 1 : 1;

            if (failures >= settings.FailuresBeforeCritical)
            {
                return new(new DampedState(Severity.Critical, null, 0), null, "unreachable",
                    $"no reply to {m.Sent} echoes in {settings.FailuresBeforeCritical} consecutive poll(s)");
            }

            return new(new DampedState(Severity.Warning, Severity.Critical, failures), null, "no reply",
                $"no reply ({failures} of {settings.FailuresBeforeCritical} failed polls before Critical)");
        }

        var latency = Ms(m.AvgMs);
        var text = m.Received < m.Sent ? $"{latency}, {m.LossPercent:0.#}% loss" : latency;
        if (m.LossPercent >= settings.WarningLossPercent)
        {
            return new(new DampedState(Severity.Warning, null, 0), m.AvgMs, text,
                $"{m.LossPercent:0.#}% loss (threshold {settings.WarningLossPercent:0.#}%)");
        }

        if (m.AvgMs >= settings.WarningLatencyMs)
        {
            return new(new DampedState(Severity.Warning, null, 0), m.AvgMs, text,
                $"average {latency} (threshold {settings.WarningLatencyMs:0.#} ms)");
        }

        return new(new DampedState(Severity.Normal, null, 0), m.AvgMs, text, $"replying, average {latency}");
    }

    private static string Ms(double? ms) => ms switch
    {
        null => "? ms",
        < 10 => ms.Value.ToString("0.#", CultureInfo.InvariantCulture) + " ms",
        _ => ms.Value.ToString("0", CultureInfo.InvariantCulture) + " ms",
    };
}
