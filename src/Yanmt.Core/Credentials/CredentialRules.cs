namespace Yanmt.Core.Credentials;

public enum CredentialKind
{
    Ssh,
    SnmpV3,
}

/// <summary>SNMPv3 USM authentication. SHA-2 only (RFC 7860); MD5 and SHA-1 are deliberately absent.</summary>
public enum SnmpAuthProtocol
{
    Sha256,
    Sha384,
    Sha512,
}

/// <summary>
/// SNMPv3 USM privacy. With SHA-256 or stronger auth the localized key is already long enough for
/// AES-256, so no (non-standard) key extension is involved for any of these.
/// </summary>
public enum SnmpPrivProtocol
{
    Aes128,
    Aes192,
    Aes256,
}

/// <summary>Validation shared by the credential profile form and the service.</summary>
public static class CredentialRules
{
    public const int MaxNameLength = 64;
    public const int MaxUsernameLength = 64;
    public const int MaxSecretLength = 128;

    /// <summary>RFC 3414 requires USM passphrases of at least 8 characters; IOS-XE enforces the same.</summary>
    public const int MinSnmpPassphraseLength = 8;

    public const int DefaultSshPort = 22;
    public const int DefaultSnmpPort = 161;

    public static int DefaultPort(CredentialKind kind) => kind == CredentialKind.Ssh ? DefaultSshPort : DefaultSnmpPort;

    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Name is required.";
        }

        return name.Trim().Length > MaxNameLength ? $"Name must be at most {MaxNameLength} characters." : null;
    }

    public static string? ValidateUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return "Username is required.";
        }

        if (username.Length > MaxUsernameLength)
        {
            return $"Username must be at most {MaxUsernameLength} characters.";
        }

        return username.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ? "Username can't contain spaces or control characters." : null;
    }

    public static string? ValidatePort(int port) =>
        port is < 1 or > 65535 ? "Port must be between 1 and 65535." : null;

    /// <summary>Validates a secret being set. Null or empty means "keep the stored value" and is checked by the caller.</summary>
    public static string? ValidateSecret(string label, string secret, CredentialKind kind)
    {
        if (secret.Length > MaxSecretLength)
        {
            return $"{label} must be at most {MaxSecretLength} characters.";
        }

        if (secret.Any(char.IsControl))
        {
            return $"{label} can't contain control characters.";
        }

        return kind == CredentialKind.SnmpV3 && secret.Length < MinSnmpPassphraseLength
            ? $"{label} must be at least {MinSnmpPassphraseLength} characters (SNMPv3 USM minimum)."
            : null;
    }

    public static string Describe(SnmpAuthProtocol auth) => auth switch
    {
        SnmpAuthProtocol.Sha256 => "SHA-256",
        SnmpAuthProtocol.Sha384 => "SHA-384",
        SnmpAuthProtocol.Sha512 => "SHA-512",
        _ => auth.ToString(),
    };

    public static string Describe(SnmpPrivProtocol priv) => priv switch
    {
        SnmpPrivProtocol.Aes128 => "AES-128",
        SnmpPrivProtocol.Aes192 => "AES-192",
        SnmpPrivProtocol.Aes256 => "AES-256",
        _ => priv.ToString(),
    };
}
