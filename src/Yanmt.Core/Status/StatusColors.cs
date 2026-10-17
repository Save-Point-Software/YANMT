using System.Text.RegularExpressions;

namespace Yanmt.Core.Status;

/// <summary>Map colors per severity. Defaults follow SNMPc: green, yellow, red, gray.</summary>
public sealed partial record StatusColors(string Normal, string Warning, string Critical, string Unknown)
{
    public static StatusColors Default { get; } = new("#2E9E44", "#F2C200", "#D92D20", "#9AA0A6");

    public string For(Severity severity) => severity switch
    {
        Severity.Normal => Normal,
        Severity.Warning => Warning,
        Severity.Critical => Critical,
        _ => Unknown,
    };

    /// <summary>True if every color is a #RRGGBB hex value (the only format the UI writes or accepts).</summary>
    public bool IsValid() =>
        HexColor().IsMatch(Normal) && HexColor().IsMatch(Warning)
        && HexColor().IsMatch(Critical) && HexColor().IsMatch(Unknown);

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();
}
