using Yanmt.Core.Csv;

namespace Yanmt.Core.Inventory;

/// <summary>
/// A node as it appears in CSV. Credential profiles are referenced by name only; there are
/// deliberately no credential or secret fields.
/// </summary>
public sealed record NodeCsvRecord(
    string Hostname,
    string ManagementIp,
    string? Site,
    IReadOnlyList<string> Tags,
    NodeIconType Icon,
    bool Enabled,
    bool IcmpEnabled,
    bool SnmpEnabled,
    bool SshEnabled,
    string? SnmpProfile = null,
    string? SshProfile = null);

/// <summary>One parsed data row: either a record or the errors that stopped it. Line is 1-based, header = line 1.</summary>
public sealed record NodeCsvRow(int Line, NodeCsvRecord? Record, IReadOnlyList<string> Errors)
{
    public bool IsValid => Record is not null && Errors.Count == 0;
}

public sealed record NodeCsvParseResult(IReadOnlyList<NodeCsvRow> Rows, IReadOnlyList<string> FileErrors)
{
    public bool HasErrors => FileErrors.Count > 0 || Rows.Any(r => !r.IsValid);
}

/// <summary>Node CSV import/export. Column order is fixed on export; import matches headers by name.</summary>
public static class NodeCsv
{
    public static readonly IReadOnlyList<string> Columns =
    [
        "hostname", "management_ip", "site", "tags", "icon",
        "enabled", "icmp_enabled", "snmp_enabled", "ssh_enabled",
        "snmp_profile", "ssh_profile",
    ];

    private static readonly string[] RequiredColumns = ["hostname", "management_ip"];

    public static string Export(IEnumerable<NodeCsvRecord> nodes)
    {
        var rows = new List<IReadOnlyList<string?>> { Columns.ToArray() };
        rows.AddRange(nodes
            .OrderBy(n => n.Hostname, StringComparer.OrdinalIgnoreCase)
            .Select(n => (IReadOnlyList<string?>)
            [
                n.Hostname, n.ManagementIp, n.Site, string.Join(';', n.Tags), n.Icon.ToString().ToLowerInvariant(),
                Bool(n.Enabled), Bool(n.IcmpEnabled), Bool(n.SnmpEnabled), Bool(n.SshEnabled),
                n.SnmpProfile, n.SshProfile,
            ]));
        return CsvCodec.Write(rows);

        static string Bool(bool b) => b ? "true" : "false";
    }

    private static string? ValidateProfileName(string column, string? name) =>
        name is not null && name.Length > Credentials.CredentialRules.MaxNameLength
            ? $"{column} must be at most {Credentials.CredentialRules.MaxNameLength} characters."
            : null;

    /// <summary>
    /// Parses and validates every row. Missing optional columns take defaults
    /// (enabled/icmp = true, snmp/ssh = false, icon = generic). Duplicate hostnames within the
    /// file are errors; duplicates against existing inventory are the caller's concern (they become updates).
    /// </summary>
    public static NodeCsvParseResult Parse(string text)
    {
        List<string[]> table;
        try
        {
            table = CsvCodec.Parse(text);
        }
        catch (FormatException ex)
        {
            return new([], [ex.Message]);
        }

        if (table.Count == 0)
        {
            return new([], ["The file is empty."]);
        }

        var header = table[0].Select(h => h.Trim().ToLowerInvariant()).ToArray();
        var fileErrors = new List<string>();
        var unknown = header.Where(h => h.Length > 0 && !Columns.Contains(h)).ToList();
        if (unknown.Count > 0)
        {
            fileErrors.Add($"Unknown column(s): {string.Join(", ", unknown)}. Expected: {string.Join(", ", Columns)}.");
        }

        var missing = RequiredColumns.Where(c => !header.Contains(c)).ToList();
        if (missing.Count > 0)
        {
            fileErrors.Add($"Missing required column(s): {string.Join(", ", missing)}.");
        }

        var duplicateHeaders = header.Where(h => h.Length > 0).GroupBy(h => h).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicateHeaders.Count > 0)
        {
            fileErrors.Add($"Duplicate column(s): {string.Join(", ", duplicateHeaders)}.");
        }

        if (fileErrors.Count > 0)
        {
            return new([], fileErrors);
        }

        var index = header.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i);
        var rows = new List<NodeCsvRow>();
        var seenHostnames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var r = 1; r < table.Count; r++)
        {
            var line = r + 1;
            var cells = table[r];
            if (cells.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var errors = new List<string>();
            if (cells.Length > header.Length)
            {
                errors.Add($"Row has {cells.Length} fields but the header has {header.Length}.");
            }

            var hostname = Get("hostname");
            var ip = Get("management_ip");
            var site = Get("site");
            var tags = NodeRules.ParseTags(Get("tags"));

            Add(NodeRules.ValidateHostname(hostname));
            Add(NodeRules.ValidateManagementIp(ip));
            Add(NodeRules.ValidateSite(site));
            foreach (var tag in tags)
            {
                Add(NodeRules.ValidateTag(tag));
            }

            var icon = NodeIconType.Generic;
            var iconText = Get("icon");
            if (!string.IsNullOrEmpty(iconText)
                && (!Enum.TryParse(iconText, ignoreCase: true, out icon) || !Enum.IsDefined(icon) || int.TryParse(iconText, out _)))
            {
                errors.Add($"Icon '{iconText}' is not one of: {string.Join(", ", Enum.GetNames<NodeIconType>().Select(n => n.ToLowerInvariant()))}.");
                icon = NodeIconType.Generic;
            }

            var enabled = ParseBool("enabled", true);
            var icmp = ParseBool("icmp_enabled", true);
            var snmp = ParseBool("snmp_enabled", false);
            var ssh = ParseBool("ssh_enabled", false);
            var snmpProfile = Get("snmp_profile");
            var sshProfile = Get("ssh_profile");
            Add(ValidateProfileName("snmp_profile", snmpProfile));
            Add(ValidateProfileName("ssh_profile", sshProfile));

            if (!string.IsNullOrEmpty(hostname))
            {
                if (seenHostnames.TryGetValue(hostname, out var firstLine))
                {
                    errors.Add($"Hostname '{hostname}' already appears on line {firstLine}.");
                }
                else
                {
                    seenHostnames[hostname] = line;
                }
            }

            var record = errors.Count == 0
                ? new NodeCsvRecord(
                    hostname!,
                    NodeRules.TryNormalizeIp(ip!, out var normalizedIp) ? normalizedIp : ip!,
                    string.IsNullOrWhiteSpace(site) ? null : site,
                    tags,
                    icon,
                    enabled,
                    icmp,
                    snmp,
                    ssh,
                    snmpProfile,
                    sshProfile)
                : null;
            rows.Add(new NodeCsvRow(line, record, errors));

            string? Get(string column)
            {
                if (!index.TryGetValue(column, out var i) || i >= cells.Length)
                {
                    return null;
                }

                var value = CsvCodec.UnescapeFormulaGuard(cells[i].Trim());
                return value.Length == 0 ? null : value;
            }

            void Add(string? error)
            {
                if (error is not null)
                {
                    errors.Add(error);
                }
            }

            bool ParseBool(string column, bool defaultValue)
            {
                var text = Get(column);
                if (text is null)
                {
                    return defaultValue;
                }

                switch (text.ToLowerInvariant())
                {
                    case "true" or "yes" or "y" or "1":
                        return true;
                    case "false" or "no" or "n" or "0":
                        return false;
                    default:
                        errors.Add($"{column} must be true/false (got '{text}').");
                        return defaultValue;
                }
            }
        }

        return new(rows, []);
    }
}
