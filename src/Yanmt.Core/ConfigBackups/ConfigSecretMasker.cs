using System.Text;
using System.Text.RegularExpressions;

namespace Yanmt.Core.ConfigBackups;

/// <summary>
/// Masks secret values in a config for display. Line count and line order never change, so diff line
/// numbers stay valid. The keyword and encryption-type prefix stay visible (<c>secret 9 &lt;masked&gt;</c>);
/// only the value is replaced. Masking is display-only: the stored text is never altered.
/// </summary>
public static partial class ConfigSecretMasker
{
    public const string Mask = "<masked>";

    public static string MaskSecrets(string config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var sb = new StringBuilder(config.Length);
        var rest = config.AsSpan();
        while (true)
        {
            var nl = rest.IndexOf('\n');
            var line = nl < 0 ? rest : rest[..nl];
            var cr = line.Length > 0 && line[^1] == '\r';
            sb.Append(MaskLine(cr ? line[..^1].ToString() : line.ToString()));
            if (cr)
            {
                sb.Append('\r');
            }

            if (nl < 0)
            {
                break;
            }

            sb.Append('\n');
            rest = rest[(nl + 1)..];
        }

        return sb.ToString();
    }

    /// <summary>True when <see cref="MaskLine"/> would change the line.</summary>
    public static bool IsSecretLine(string line) => !string.Equals(MaskLine(line), line, StringComparison.Ordinal);

    public static string MaskLine(string line)
    {
        // Structure lines that mention "key" but carry no secret.
        if (NotSecret().IsMatch(line))
        {
            return line;
        }

        // snmp-server user ... auth <algo> <secret> priv <algo> [bits] <secret>: mask values mid-line.
        if (SnmpUser().IsMatch(line))
        {
            line = SnmpAuth().Replace(line, m => m.Groups[1].Value + Mask);
            line = SnmpPriv().Replace(line, m => m.Groups[1].Value + Mask);
            return line;
        }

        // password / secret / key-style keywords: mask everything after the optional type digit.
        var match = PasswordOrSecret().Match(line);
        if (!match.Success)
        {
            match = KeyValue().Match(line);
        }

        if (!match.Success)
        {
            match = Community().Match(line);
        }

        return match.Success ? string.Concat(line.AsSpan(0, match.Groups[1].Length), Mask) : line;
    }

    // Anchored group 1 = everything up to the value, so replacement is line[..group1.Length] + mask.
    [GeneratedRegex(@"^(.*?(?<![\w-])(?:password|secret)\s+(?:level\s+\d+\s+)?(?:\d\s+)?)\S.*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PasswordOrSecret();

    [GeneratedRegex(@"^(.*?(?<![\w-])(?:pre-shared-key|authentication-key|key-string|key)\s+(?:(?:local|remote)\s+)?(?:\d\s+)?)(?!chain\b)\S.*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"^(\s*snmp-server\s+community\s+)\S.*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Community();

    [GeneratedRegex(@"^\s*snmp-server\s+user\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SnmpUser();

    [GeneratedRegex(@"(\bauth\s+(?:md5|sha\S*)\s+(?:(?:0|7|encrypted)\s+)?)\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SnmpAuth();

    [GeneratedRegex(@"(\bpriv\s+(?:des|3des|aes)\s*(?:\d+\s+)?(?:(?:0|7|encrypted)\s+)?)\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SnmpPriv();

    // "key chain NAME", "key 1" (a key-chain id), "crypto key generate ...", "ip ssh pubkey-chain".
    [GeneratedRegex(@"^\s*(?:key\s+chain\b|key\s+\d+\s*$|crypto\s+key\b|crypto\s+pki\b|ip\s+ssh\b|ip\s+domain)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotSecret();
}
