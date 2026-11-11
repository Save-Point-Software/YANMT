using System.Globalization;
using Yanmt.Core.Status;

namespace Yanmt.Core.Monitors;

/// <summary>Describes a state change that is still waiting on consecutive-match dampening.</summary>
public static class PendingText
{
    /// <summary>
    /// E.g. "Critical pending (1 of 2 matching polls before Critical)". Null when nothing is
    /// pending: no candidate, the candidate is the current state, or no dampening (1 match).
    /// </summary>
    public static string? For(Severity current, Severity? pending, int count, int required)
    {
        if (pending is not { } p || p == current || required <= 1 || count < 1)
        {
            return null;
        }

        var inv = CultureInfo.InvariantCulture;
        var seen = Math.Min(count, required - 1).ToString(inv);
        return $"{p} pending ({seen} of {required.ToString(inv)} matching polls before {p})";
    }
}
