using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Yanmt.Core.Inventory;

/// <summary>Validation rules for node fields, shared by the edit form and CSV import.</summary>
public static partial class NodeRules
{
    public const int MaxHostnameLength = 253;
    public const int MaxSiteLength = 100;
    public const int MaxTagLength = 50;

    /// <summary>Hostnames as IOS-XE and DNS allow them: letters, digits, dot, hyphen, underscore.</summary>
    public static string? ValidateHostname(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return "Hostname is required.";
        }

        if (hostname.Length > MaxHostnameLength)
        {
            return $"Hostname must be {MaxHostnameLength} characters or fewer.";
        }

        return HostnamePattern().IsMatch(hostname)
            ? null
            : "Hostname may only contain letters, digits, '.', '-' and '_', and must start with a letter or digit.";
    }

    /// <summary>Accepts a literal IPv4 or IPv6 address (no hostnames, no CIDR, no zone/scope ids).</summary>
    public static string? ValidateManagementIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return "Management IP is required.";
        }

        return TryNormalizeIp(ip, out _) ? null : $"'{ip}' is not a valid IPv4 or IPv6 address.";
    }

    /// <summary>
    /// Parses and canonicalizes an address. IPv4 must be dotted-quad: <see cref="IPAddress.TryParse(string?, out IPAddress?)"/>
    /// alone would accept forms like "10" or "10.1" that are easy to mistype.
    /// </summary>
    public static bool TryNormalizeIp(string ip, out string normalized)
    {
        normalized = string.Empty;
        var trimmed = ip.Trim();
        if (!IPAddress.TryParse(trimmed, out var address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork && !DottedQuad().IsMatch(trimmed))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
        {
            return false;
        }

        normalized = address.ToString();
        return true;
    }

    public static string? ValidateSite(string? site) =>
        site is { Length: > MaxSiteLength } ? $"Site must be {MaxSiteLength} characters or fewer." : null;

    public static string? ValidateTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return "Tags cannot be blank.";
        }

        if (tag.Length > MaxTagLength)
        {
            return $"Tag '{tag}' must be {MaxTagLength} characters or fewer.";
        }

        return TagPattern().IsMatch(tag)
            ? null
            : $"Tag '{tag}' may only contain letters, digits, '.', '-', '_' and spaces.";
    }

    /// <summary>Splits a tag list (';' or ',' separated), trims, and de-duplicates case-insensitively.</summary>
    public static IReadOnlyList<string> ParseTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return [];
        }

        return tags.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]*$")]
    private static partial Regex HostnamePattern();

    [GeneratedRegex(@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$")]
    private static partial Regex DottedQuad();

    [GeneratedRegex(@"^[A-Za-z0-9._\- ]+$")]
    private static partial Regex TagPattern();
}
