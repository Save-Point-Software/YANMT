using System.Globalization;

namespace Yanmt.Core.Monitoring;

/// <summary>Display formatting shared by labels, tables and charts. Always invariant culture.</summary>
public static class Units
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>TimeTicks (1/100 s) as "12d 3h 4m", "3h 4m" or "4m 12s".</summary>
    public static string Uptime(uint ticks)
    {
        var t = TimeSpan.FromSeconds(ticks / 100);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m"
            : t.TotalHours >= 1 ? $"{t.Hours}h {t.Minutes}m"
            : $"{t.Minutes}m {t.Seconds}s";
    }

    /// <summary>Bits per second with SI prefixes, e.g. "12.3 Mb/s".</summary>
    public static string BitsPerSecond(double bps) => Scaled(bps, 1000, ["b/s", "kb/s", "Mb/s", "Gb/s", "Tb/s"]);

    /// <summary>Interface speed from ifHighSpeed (Mb/s).</summary>
    public static string SpeedMbps(double mbps) => mbps <= 0 ? "" : BitsPerSecond(mbps * 1_000_000).Replace("/s", "", StringComparison.Ordinal);

    public static string Bytes(double bytes) => Scaled(bytes, 1024, ["B", "KiB", "MiB", "GiB", "TiB"]);

    private static string Scaled(double value, double step, string[] units)
    {
        var i = 0;
        while (Math.Abs(value) >= step && i < units.Length - 1)
        {
            value /= step;
            i++;
        }

        var format = i == 0 || Math.Abs(value) >= 100 ? "0" : Math.Abs(value) >= 10 ? "0.#" : "0.##";
        return value.ToString(format, Inv) + " " + units[i];
    }
}

/// <summary>Counter arithmetic for rates (interfaces now, Rate-mode custom monitors in Phase 4).</summary>
public static class RateMath
{
    /// <summary>
    /// The increase from <paramref name="previous"/> to <paramref name="current"/>. A Counter32
    /// that went down wrapped once (adds 2^32). A Counter64 going down is a reset (or a reboot), not
    /// a wrap: there's no trustworthy delta, so null.
    /// </summary>
    public static decimal? Delta(decimal previous, decimal current, int counterBits)
    {
        if (current >= previous)
        {
            return current - previous;
        }

        return counterBits == 32 && previous <= uint.MaxValue ? current + 4294967296m - previous : null;
    }

    /// <summary>Bits per second from two octet counter samples taken <paramref name="seconds"/> apart.</summary>
    public static double? BitsPerSecond(decimal previous, decimal current, int counterBits, double seconds) =>
        seconds <= 0 || Delta(previous, current, counterBits) is not { } delta ? null : (double)delta * 8 / seconds;
}
