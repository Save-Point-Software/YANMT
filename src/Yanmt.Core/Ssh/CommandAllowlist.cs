using System.Globalization;
using System.Text.RegularExpressions;

namespace Yanmt.Core.Ssh;

/// <summary>
/// The only commands YANMT may ever send over SSH. The device account is a plain privilege-15 user,
/// so this list is the read-only enforcement: matching is exact and case-sensitive, with no
/// trimming, abbreviations, pipes, or separators. Anything else is rejected before it is sent.
/// Never add a command here that can change device state.
/// </summary>
public static partial class CommandAllowlist
{
    public const string ShowRunningConfig = "show running-config";
    public const string ShowVersion = "show version";
    public const string TerminalLengthZero = "terminal length 0";

    /// <summary>Largest value IOS-XE accepts for <c>terminal width</c>.</summary>
    public const int MaxTerminalWidth = 512;

    private static readonly HashSet<string> Exact = new(StringComparer.Ordinal)
    {
        ShowRunningConfig,
        ShowVersion,
        TerminalLengthZero,
    };

    public static IReadOnlyCollection<string> ExactCommands => Exact;

    /// <summary>The allowlisted <c>terminal width</c> command for a width of 0 (unlimited) to 512.</summary>
    public static string TerminalWidth(int width)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, MaxTerminalWidth);
        return "terminal width " + width.ToString(CultureInfo.InvariantCulture);
    }

    public static bool IsAllowed(string? command)
    {
        if (command is null)
        {
            return false;
        }

        if (Exact.Contains(command))
        {
            return true;
        }

        var match = TerminalWidthPattern().Match(command);
        return match.Success
               && int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var width)
               && width <= MaxTerminalWidth;
    }

    /// <summary>Throws <see cref="CommandNotAllowedException"/> unless the command is allowlisted.</summary>
    public static void EnsureAllowed(string? command)
    {
        if (!IsAllowed(command))
        {
            throw new CommandNotAllowedException(command);
        }
    }

    // ASCII digits only, no leading zeros, nothing before or after. \z rather than $ so a trailing
    // newline can't sneak through.
    [GeneratedRegex(@"\Aterminal width (0|[1-9][0-9]{0,2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex TerminalWidthPattern();
}

/// <summary>Thrown when code asks to send an SSH command that is not on <see cref="CommandAllowlist"/>.</summary>
public sealed class CommandNotAllowedException(string? command)
    : InvalidOperationException($"Refused to send SSH command {Describe(command)}: it is not on the read-only allowlist.")
{
    public string? Command { get; } = command;

    // Shows control characters so an injected newline is visible in the log.
    private static string Describe(string? command) => command is null
        ? "(null)"
        : "'" + string.Concat(command.Take(80).Select(c => char.IsControl(c) ? $"\\x{(int)c:X2}" : c.ToString())) + (command.Length > 80 ? "…'" : "'");
}
