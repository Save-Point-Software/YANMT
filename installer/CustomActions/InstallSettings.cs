using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using WixToolset.Dtf.WindowsInstaller;

namespace Yanmt.Installer.CustomActions;

/// <summary>A role group from ADMIN_GROUPS or VIEWER_GROUPS, and the local group to create if it's missing.</summary>
internal sealed class GroupEntry
{
    public GroupEntry(string configured, bool isAdmin)
    {
        Configured = configured;
        IsAdmin = isAdmin;
    }

    public string Configured { get; }
    public bool IsAdmin { get; }

    /// <summary>Set when the entry is a missing local group (".\Name" or "THISPC\Name") that the MSI creates.</summary>
    public string? CreateLocalName { get; set; }
}

/// <summary>
/// The installer properties, checked. Group lists use the same forms as Yanmt:Roles (see
/// RoleGroupMap.TryResolve in the app): "DOMAIN\Group", ".\Group" or "THISPC\Group" (local), "Group", or a SID.
/// </summary>
internal sealed class InstallSettings
{
    public const int ThumbprintLength = 40;

    public int Port { get; private set; }
    public string Thumbprint { get; private set; } = "";
    public List<GroupEntry> AdminGroups { get; } = [];
    public List<GroupEntry> ViewerGroups { get; } = [];
    public string FirewallRemoteAddress { get; private set; } = "";
    public List<string> Errors { get; } = [];

    public IEnumerable<GroupEntry> AllGroups => AdminGroups.Concat(ViewerGroups);

    /// <summary>Reads and checks the properties. Problems go to <see cref="Errors"/>; nothing is changed on the machine.</summary>
    public static InstallSettings Read(Session session)
    {
        var s = new InstallSettings();
        s.ReadPort(session["PORT"]);
        s.ReadThumbprint(session["CERT_THUMBPRINT"]);
        s.ReadGroups(session["ADMIN_GROUPS"], isAdmin: true);
        s.ReadGroups(session["VIEWER_GROUPS"], isAdmin: false);
        if (s.AdminGroups.Count == 0)
        {
            s.Errors.Add("ADMIN_GROUPS is required: at least one group (for example CONTOSO\\YANMT-Admins or .\\YANMT-Admins).");
        }

        s.ReadFirewallRemoteAddress(session["FIREWALL_REMOTE_ADDRESS"]);
        return s;
    }

    /// <summary>The properties as the MSI writes them to HKLM\SOFTWARE\YANMT (normalized).</summary>
    public void WriteBack(Session session)
    {
        session["PORT"] = Port.ToString(CultureInfo.InvariantCulture);
        session["CERT_THUMBPRINT"] = Thumbprint;
        session["ADMIN_GROUPS"] = string.Join(";", AdminGroups.Select(g => g.Configured));
        session["VIEWER_GROUPS"] = string.Join(";", ViewerGroups.Select(g => g.Configured));
        session["FIREWALL_REMOTE_ADDRESS"] = FirewallRemoteAddress;
    }

    private void ReadPort(string value)
    {
        var text = (value ?? "").Trim();
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port < 1 || port > 65535)
        {
            Errors.Add($"PORT '{text}' is not a TCP port (1-65535).");
            return;
        }

        Port = port;
    }

    /// <summary>Same normalization as the app (Yanmt.Core.Installer.InstallerSettings): spaces and invisible marks dropped.</summary>
    internal static string? NormalizeThumbprint(string value, out string? error)
    {
        error = null;
        var s = value ?? "";
        var stray = s.Any(c => !Uri.IsHexDigit(c) && !char.IsWhiteSpace(c)
            && char.GetUnicodeCategory(c) != UnicodeCategory.Format);
        var hex = new string(s.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (stray || hex.Length != ThumbprintLength)
        {
            error = $"CERT_THUMBPRINT must be {ThumbprintLength} hex digits (spaces are ignored).";
            return null;
        }

        return hex;
    }

    private void ReadThumbprint(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Errors.Add(@"CERT_THUMBPRINT is required: the thumbprint of the HTTPS certificate in LocalMachine\My.");
            return;
        }

        var thumbprint = NormalizeThumbprint(value, out var error);
        if (thumbprint is null)
        {
            Errors.Add(error!);
            return;
        }

        Thumbprint = thumbprint;
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        try
        {
            if (found.Count == 0)
            {
                Errors.Add($@"No certificate with thumbprint {thumbprint} in LocalMachine\My. Import the certificate first (DEPLOYMENT.md, Certificate).");
            }
            else if (!found[0].HasPrivateKey)
            {
                Errors.Add($@"The certificate {thumbprint} in LocalMachine\My has no private key, so YANMT can't serve HTTPS with it.");
            }
        }
        finally
        {
            foreach (var cert in found)
            {
                cert.Reset();
            }
        }
    }

    private void ReadGroups(string value, bool isAdmin)
    {
        var list = isAdmin ? AdminGroups : ViewerGroups;
        var property = isAdmin ? "ADMIN_GROUPS" : "VIEWER_GROUPS";
        foreach (var name in (value ?? "").Split(';').Select(p => p.Trim()).Where(p => p.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var entry = new GroupEntry(name, isAdmin);
            var error = Check(entry);
            if (error is null)
            {
                list.Add(entry);
            }
            else
            {
                Errors.Add($"{property}: {error}");
            }
        }
    }

    /// <summary>Null if the entry resolves or is a local group the MSI will create.</summary>
    private static string? Check(GroupEntry entry)
    {
        var name = entry.Configured;
        if (name.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _ = new SecurityIdentifier(name);
                return null;
            }
            catch (ArgumentException)
            {
                return $"'{name}' is not a valid SID.";
            }
        }

        var slash = name.IndexOf('\\');
        var prefix = slash < 0 ? null : name.Substring(0, slash);
        var account = slash < 0 ? name : name.Substring(slash + 1);
        var isLocal = prefix == "." || string.Equals(prefix, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        var lookup = prefix == "." ? Environment.MachineName + "\\" + account : name;

        if (Resolves(lookup))
        {
            return null;
        }

        if (isLocal)
        {
            if (!IsValidLocalGroupName(account))
            {
                return $"'{name}' is not a valid local group name.";
            }

            entry.CreateLocalName = account;
            return null;
        }

        return slash < 0
            ? $"'{name}' was not found. For a local group the installer should create, write .\\{name}."
            : $"'{name}' was not found. The installer creates missing local groups (.\\Name) only, never domain groups.";
    }

    private static bool Resolves(string name)
    {
        try
        {
            _ = new NTAccount(name).Translate(typeof(SecurityIdentifier));
            return true;
        }
        catch (SystemException)
        {
            // IdentityNotMappedException (no such account), ArgumentException (malformed name),
            // or a domain trust failure: all mean "doesn't resolve".
            return false;
        }
    }

    /// <summary>Windows local group names: 1-256 characters, none of "/\[]:|&lt;&gt;+=;?,*@, not only dots or spaces.</summary>
    internal static bool IsValidLocalGroupName(string name) =>
        name.Length > 0 && name.Length <= 256
        && name.IndexOfAny("\"/\\[]:|<>+=;?,*@".ToCharArray()) < 0
        && name.Any(c => c != '.' && c != ' ')
        && !name.Any(char.IsControl);

    /// <summary>
    /// Empty means no firewall rule. Otherwise a comma-separated list of addresses, subnets (a.b.c.d/nn),
    /// ranges (a-b), or LocalSubnet, as New-NetFirewallRule -RemoteAddress takes them. "Any" is refused:
    /// the rule exists to limit who can reach the UI.
    /// </summary>
    private void ReadFirewallRemoteAddress(string value)
    {
        var parts = (value ?? "").Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        foreach (var part in parts)
        {
            if (!IsRemoteAddress(part))
            {
                Errors.Add($"FIREWALL_REMOTE_ADDRESS: '{part}' is not an address, subnet (192.0.2.0/24), range (192.0.2.10-192.0.2.20) or LocalSubnet. Leave it empty for no firewall rule.");
            }
        }

        FirewallRemoteAddress = string.Join(",", parts);
    }

    internal static bool IsRemoteAddress(string part)
    {
        if (string.Equals(part, "LocalSubnet", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var dash = part.IndexOf('-');
        if (dash > 0)
        {
            return IsIp(part.Substring(0, dash)) && IsIp(part.Substring(dash + 1));
        }

        var slash = part.IndexOf('/');
        if (slash > 0)
        {
            var ip = part.Substring(0, slash);
            var suffix = part.Substring(slash + 1);
            if (!IsIp(ip))
            {
                return false;
            }

            var max = IPAddress.Parse(ip).AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
            return (int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) && prefix >= 0 && prefix <= max)
                || (max == 32 && IsIp(suffix));
        }

        return IsIp(part);
    }

    /// <summary>IPAddress.TryParse also takes "1" or "1.2"; require a full dotted IPv4 or an IPv6 address.</summary>
    private static bool IsIp(string text) =>
        IPAddress.TryParse(text, out var address)
        && (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 || text.Count(c => c == '.') == 3);
}
