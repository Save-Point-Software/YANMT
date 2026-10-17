namespace Yanmt.Core.Status;

public static class StatusRollup
{
    /// <summary>
    /// Worst state among the given states. An empty set means there is nothing reporting,
    /// which is <see cref="Severity.Unknown"/>.
    /// </summary>
    public static Severity Worst(IEnumerable<Severity> states)
    {
        Severity? worst = null;
        foreach (var state in states)
        {
            worst = worst is null ? state : worst.Value.WorseOf(state);
            if (worst == Severity.Critical)
            {
                break;
            }
        }

        return worst ?? Severity.Unknown;
    }
}
