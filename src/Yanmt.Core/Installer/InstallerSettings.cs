namespace Yanmt.Core.Installer;

/// <summary>
/// Settings the MSI writes to HKLM\SOFTWARE\YANMT (installer properties PORT, CERT_THUMBPRINT,
/// ADMIN_GROUPS, VIEWER_GROUPS), mapped to configuration keys. They layer above appsettings.json and
/// below environment variables. A missing or empty value sets nothing, so a manual install (no key)
/// and an install without VIEWER_GROUPS keep whatever appsettings.json says.
/// </summary>
public static class InstallerSettings
{
    /// <summary>Under HKEY_LOCAL_MACHINE, 64-bit view.</summary>
    public const string RegistryKeyPath = @"SOFTWARE\YANMT";

    public const string PortValue = "Port";
    public const string CertificateThumbprintValue = "CertificateThumbprint";
    public const string AdminGroupsValue = "AdminGroups";
    public const string ViewerGroupsValue = "ViewerGroups";

    public const string PortKey = "Yanmt:Https:Port";
    public const string CertificateThumbprintKey = "Yanmt:Https:CertificateThumbprint";
    public const string AdminGroupsKey = "Yanmt:Roles:AdminGroups";
    public const string ViewerGroupsKey = "Yanmt:Roles:ViewerGroups";

    /// <summary>
    /// Maps registry values (REG_SZ as string, REG_DWORD as int, REG_MULTI_SZ as string[]) to
    /// configuration keys. Group lists are ';'-separated in a REG_SZ, or one per line in a REG_MULTI_SZ.
    /// </summary>
    /// <param name="values">Value name to value; names are case-insensitive like the registry.</param>
    /// <param name="lowerAdminCount">AdminGroups entries already present in lower layers.</param>
    /// <param name="lowerViewerCount">ViewerGroups entries already present in lower layers.</param>
    /// <remarks>
    /// Configuration merges arrays by index, so a shorter list cannot hide the extra entries of a
    /// longer one below it. The remaining indexes are filled with the list's last entry; role mapping
    /// resolves names to a set of SIDs, so the repeats grant nothing extra.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A value is present but invalid.</exception>
    public static IReadOnlyDictionary<string, string?> ToConfiguration(
        IReadOnlyDictionary<string, object?> values, int lowerAdminCount = 0, int lowerViewerCount = 0)
    {
        var lookup = new Dictionary<string, object?>(values, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (lookup.TryGetValue(PortValue, out var port) && ParsePort(port) is { } p)
        {
            result[PortKey] = p.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (lookup.TryGetValue(CertificateThumbprintValue, out var thumbprint)
            && NormalizeThumbprint(thumbprint) is { Length: > 0 } t)
        {
            result[CertificateThumbprintKey] = t;
        }

        AddList(result, AdminGroupsKey, lookup.GetValueOrDefault(AdminGroupsValue), lowerAdminCount);
        AddList(result, ViewerGroupsKey, lookup.GetValueOrDefault(ViewerGroupsValue), lowerViewerCount);
        return result;
    }

    /// <summary>Splits a group list: ';'-separated string or string[]; trims and drops empty entries.</summary>
    public static IReadOnlyList<string> SplitGroups(object? value) => value switch
    {
        string s => Clean(s.Split(';')),
        IEnumerable<string> many => Clean(many.SelectMany(m => (m ?? "").Split(';'))),
        null => [],
        _ => throw new InvalidOperationException(
            $"Installer setting has an unsupported registry type ({value.GetType().Name}); expected a string or multi-string."),
    };

    private static List<string> Clean(IEnumerable<string> parts) =>
        parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

    private static int? ParsePort(object? value)
    {
        var text = value switch
        {
            null => null,
            int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string s => s.Trim(),
            _ => throw Invalid(PortValue, $"unsupported registry type {value.GetType().Name}"),
        };
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            throw Invalid(PortValue, $"'{text}' is not a TCP port (1-65535)");
        }

        return port;
    }

    /// <summary>SHA-1 thumbprint as shown by Windows: 40 hex digits.</summary>
    private const int ThumbprintLength = 40;

    private static string? NormalizeThumbprint(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is not string s)
        {
            throw Invalid(CertificateThumbprintValue, $"unsupported registry type {value.GetType().Name}");
        }

        // Copying from the certificate dialog adds spaces and sometimes an invisible left-to-right mark.
        var stray = s.Where(c => !char.IsAsciiHexDigit(c) && !char.IsWhiteSpace(c)
            && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format);
        var hex = new string(s.Where(char.IsAsciiHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length == 0 && !stray.Any())
        {
            return null;
        }

        if (stray.Any() || hex.Length != ThumbprintLength)
        {
            throw Invalid(CertificateThumbprintValue, $"expected {ThumbprintLength} hex digits");
        }

        return hex;
    }

    private static void AddList(Dictionary<string, string?> result, string key, object? value, int lowerCount)
    {
        var groups = SplitGroups(value);
        if (groups.Count == 0)
        {
            return;
        }

        for (var i = 0; i < Math.Max(groups.Count, lowerCount); i++)
        {
            result[$"{key}:{i}"] = groups[Math.Min(i, groups.Count - 1)];
        }
    }

    private static InvalidOperationException Invalid(string name, string why) =>
        new($@"Installer setting HKLM\{RegistryKeyPath}\{name} is invalid: {why}. Re-run the YANMT installer (Change) to fix it.");
}
