namespace Yanmt.Core.Credentials;

public enum CredentialKind
{
    Ssh,
    SnmpV3,
}

/// <summary>
/// SNMPv3 USM authentication: SHA-2 (RFC 7860), plus SHA-1 (HMAC-SHA-96, RFC 3414) as a legacy
/// option for devices without SHA-2, allowed only with AES-128. MD5 is deliberately absent.
/// </summary>
public enum SnmpAuthProtocol
{
    Sha256,
    Sha384,
    Sha512,
    Sha1,
}

/// <summary>
/// SNMPv3 USM privacy. DES is deliberately absent. Only AES-128 is verified against IOS-XE: in the
/// CML lab SHA-256/AES-256 was refused with usmStatsWrongDigests even with re-entered passwords, so
/// AES-192/256 stay selectable but are flagged as not verified (see docs/SPEC.md).
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

    /// <summary>SHA-1 can't localize a key long enough for AES-192/256 without a non-standard extension.</summary>
    public static string? ValidateSnmpProtocols(SnmpAuthProtocol auth, SnmpPrivProtocol priv) =>
        auth == SnmpAuthProtocol.Sha1 && priv != SnmpPrivProtocol.Aes128
            ? "SHA-1 (legacy) can only be paired with AES-128."
            : null;

    public static bool IsLegacy(SnmpAuthProtocol auth) => auth == SnmpAuthProtocol.Sha1;

    /// <summary>True for the privacy protocols proven against IOS-XE in the lab (AES-128 only, so far).</summary>
    public static bool IsVerifiedOnIosXe(SnmpPrivProtocol priv) => priv == SnmpPrivProtocol.Aes128;

    /// <summary>Describe, plus the "legacy" / "not verified" flags, for pickers and lists.</summary>
    public static string Label(SnmpAuthProtocol auth) => IsLegacy(auth) ? $"{Describe(auth)} (legacy)" : Describe(auth);

    public static string Label(SnmpPrivProtocol priv) => IsVerifiedOnIosXe(priv) ? Describe(priv) : $"{Describe(priv)} (not verified on IOS-XE)";

    public static string Describe(SnmpAuthProtocol auth) => auth switch
    {
        SnmpAuthProtocol.Sha1 => "SHA-1",
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
