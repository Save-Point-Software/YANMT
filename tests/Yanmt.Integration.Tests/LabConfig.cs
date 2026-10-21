using System.Text.Json;
using Yanmt.Core.Credentials;
using Yanmt.Infrastructure.Credentials;

namespace Yanmt.Integration.Tests;

/// <summary>
/// The CML lab the opt-in integration tests target. Loaded from tests/lab.json (gitignored), or
/// from the path in the YANMT_LAB_CONFIG environment variable. Copy tests/lab.example.json to start.
/// lab.json holds lab-only credentials; never commit it.
/// </summary>
public sealed class LabConfig
{
    public List<LabNode> Nodes { get; set; } = [];

    /// <summary>SNMPv3 users, one per auth/priv pairing under test.</summary>
    public LabSnmp? Snmp { get; set; }

    public LabSsh? Ssh { get; set; }

    private static readonly Lazy<LabConfig?> Cached = new(Load);

    /// <summary>The lab config, or skips the calling test when there is none.</summary>
    public static LabConfig Require()
    {
        Assert.SkipWhen(Cached.Value is null, "No lab config: copy tests/lab.example.json to tests/lab.json (or set YANMT_LAB_CONFIG).");
        return Cached.Value!;
    }

    private static LabConfig? Load()
    {
        var path = Environment.GetEnvironmentVariable("YANMT_LAB_CONFIG") is { Length: > 0 } env ? env : FindDefault();
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        var lab = JsonSerializer.Deserialize<LabConfig>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                  ?? throw new InvalidOperationException($"{path} is empty or not valid JSON.");
        lab.Validate(path);
        return lab;
    }

    /// <summary>Nodes whose snmpProfiles list includes this profile key.</summary>
    public IReadOnlyList<LabNode> NodesFor(string snmpProfile) =>
        Nodes.Where(n => n.SnmpProfiles.Contains(snmpProfile, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>A typo in a profile key would silently skip tests, so it's an error instead.</summary>
    private void Validate(string path)
    {
        foreach (var node in Nodes)
        {
            foreach (var key in node.SnmpProfiles)
            {
                if (!LabSnmp.Keys.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"{path}: node {node.Hostname} lists unknown SNMP profile '{key}'. Known: {string.Join(", ", LabSnmp.Keys)}.");
                }

                if (Snmp?.Profile(key) is null)
                {
                    throw new InvalidOperationException($"{path}: node {node.Hostname} lists SNMP profile '{key}', but snmp.{key} isn't defined.");
                }
            }
        }
    }

    private static string? FindDefault()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Yanmt.slnx")))
            {
                return Path.Combine(dir.FullName, "tests", "lab.json");
            }
        }

        return null;
    }
}

public sealed class LabNode
{
    public string Hostname { get; set; } = "";
    public string ManagementIp { get; set; } = "";

    /// <summary>The SNMP profile keys (see LabSnmp.Keys) this node is configured for. Empty: no SNMP tests.</summary>
    public List<string> SnmpProfiles { get; set; } = [];
}

/// <summary>One SNMPv3 user per auth/priv pairing; nodes opt in to each by key.</summary>
public sealed class LabSnmp
{
    public const string Sha256Aes128Key = "sha256Aes128";
    public const string Sha1Aes128Key = "sha1Aes128";
    public const string Sha256Aes256Key = "sha256Aes256";

    public static readonly string[] Keys = [Sha256Aes128Key, Sha1Aes128Key, Sha256Aes256Key];

    public int Port { get; set; } = 161;
    public LabSnmpUser? Sha256Aes128 { get; set; }

    /// <summary>Legacy SHA-1 (HMAC-SHA-96) with AES-128, for devices without SHA-2.</summary>
    public LabSnmpUser? Sha1Aes128 { get; set; }

    /// <summary>Not supported in v1 (IOS-XE refused it in the lab); its test is skipped.</summary>
    public LabSnmpUser? Sha256Aes256 { get; set; }

    /// <summary>The auth/priv pairing a profile key stands for.</summary>
    public static (SnmpAuthProtocol Auth, SnmpPrivProtocol Priv) PairingOf(string key) => key.ToLowerInvariant() switch
    {
        "sha1aes128" => (SnmpAuthProtocol.Sha1, SnmpPrivProtocol.Aes128),
        "sha256aes256" => (SnmpAuthProtocol.Sha256, SnmpPrivProtocol.Aes256),
        _ => (SnmpAuthProtocol.Sha256, SnmpPrivProtocol.Aes128),
    };

    /// <summary>The credential for a profile key, or null if it isn't defined.</summary>
    public SnmpV3Credential? Credential(string key)
    {
        var (auth, priv) = PairingOf(key);
        return Profile(key) is { } u ? new SnmpV3Credential(u.User, auth, u.AuthPassword, priv, u.PrivPassword) : null;
    }

    public LabSnmpUser? Profile(string key) => key.ToLowerInvariant() switch
    {
        "sha256aes128" => Sha256Aes128,
        "sha1aes128" => Sha1Aes128,
        "sha256aes256" => Sha256Aes256,
        _ => null,
    };
}

public sealed class LabSnmpUser
{
    public string User { get; set; } = "";
    public string AuthPassword { get; set; } = "";
    public string PrivPassword { get; set; } = "";

    public override string ToString() => $"LabSnmpUser({User}, ***)";
}

public sealed class LabSsh
{
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    public override string ToString() => $"LabSsh({Username}, ***)";
}
