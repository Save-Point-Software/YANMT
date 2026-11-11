using System.Text.RegularExpressions;

namespace Yanmt.Core.Status;

/// <summary>
/// Map colors. Severities (sources, links) use green, yellow, red, gray, as in SNMPc; node colors
/// add orange for "No SNMP". <see cref="Orange"/> has a default, so a setting saved before it
/// existed still loads (System.Text.Json uses the parameter default for a missing property).
/// </summary>
public sealed partial record StatusColors(string Normal, string Warning, string Critical, string Unknown, string Orange = StatusColors.DefaultOrange)
{
    public const string DefaultOrange = "#F28C28";

    public static StatusColors Default { get; } = new("#2E9E44", "#F2C200", "#D92D20", "#9AA0A6", DefaultOrange);

    /// <summary>Node color: Down red, No SNMP orange, Needs attention yellow, Normal green, No data gray.</summary>
    public string For(NodeColor color) => color switch
    {
        NodeColor.Down => Critical,
        NodeColor.NoSnmp => Orange,
        NodeColor.Attention => Warning,
        NodeColor.Normal => Normal,
        _ => Unknown,
    };

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
        && HexColor().IsMatch(Critical) && HexColor().IsMatch(Unknown) && HexColor().IsMatch(Orange ?? "");

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();
}
