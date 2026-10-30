namespace Yanmt.Core.Retention;

/// <summary>
/// VACUUM rewrites the whole database under an exclusive lock, which on a large database outlasts the pollers'
/// 5 s busy_timeout. So it only runs when enough of the file is free pages for compaction to be worth that.
/// </summary>
public static class VacuumPolicy
{
    /// <summary>Free pages as a fraction of all pages above which VACUUM runs.</summary>
    public const double FreeFractionThreshold = 0.25;

    public static bool ShouldVacuum(long freelistCount, long pageCount) =>
        pageCount > 0 && (double)freelistCount / pageCount > FreeFractionThreshold;
}
