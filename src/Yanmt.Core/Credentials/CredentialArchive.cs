using System.Security.Cryptography;
using System.Text.Json;

namespace Yanmt.Core.Credentials;

/// <summary>One profile in an export. Secret fields not used by the kind are null.</summary>
public sealed record ArchivedProfile(
    string Name,
    CredentialKind Kind,
    string Username,
    int Port,
    SnmpAuthProtocol? SnmpAuth,
    SnmpPrivProtocol? SnmpPriv,
    string? Password,
    string? AuthPassword,
    string? PrivPassword)
{
    public override string ToString() => $"ArchivedProfile({Name}, secrets=***)";

    /// <summary>Same rules as the edit form; null when the profile is acceptable.</summary>
    public string? Validate()
    {
        var basic = CredentialRules.ValidateName(Name) ?? CredentialRules.ValidateUsername(Username) ?? CredentialRules.ValidatePort(Port);
        if (basic is not null)
        {
            return basic;
        }

        if (!Enum.IsDefined(Kind))
        {
            return "Unknown profile type.";
        }

        if (Kind == CredentialKind.Ssh)
        {
            return Password is null ? "Password is missing." : CredentialRules.ValidateSecret("Password", Password, Kind);
        }

        if (SnmpAuth is not { } auth || SnmpPriv is not { } priv || !Enum.IsDefined(auth) || !Enum.IsDefined(priv))
        {
            return "SNMPv3 protocols are missing or unknown.";
        }

        return CredentialRules.ValidateSnmpProtocols(auth, priv)
            ?? (AuthPassword is null || PrivPassword is null ? "Auth or privacy password is missing."
                : CredentialRules.ValidateSecret("Auth password", AuthPassword, Kind) ?? CredentialRules.ValidateSecret("Privacy password", PrivPassword, Kind));
    }
}

/// <summary>Serializes profiles to and from the passphrase-encrypted export file.</summary>
public static class CredentialArchive
{
    public const int Version = 1;

    private sealed record Document(int Version, List<ArchivedProfile> Profiles);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static byte[] Seal(IReadOnlyList<ArchivedProfile> profiles, string passphrase, int iterations = PassphraseCrypto.DefaultIterations)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Document(Version, [.. profiles]), Json);
        try
        {
            return PassphraseCrypto.Encrypt(plain, passphrase, iterations);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Null for a wrong passphrase or modified file.</summary>
    /// <exception cref="InvalidDataException">Not an export, or a newer/corrupt payload.</exception>
    public static IReadOnlyList<ArchivedProfile>? Open(ReadOnlySpan<byte> file, string passphrase)
    {
        var plain = PassphraseCrypto.Decrypt(file, passphrase);
        if (plain is null)
        {
            return null;
        }

        try
        {
            var doc = JsonSerializer.Deserialize<Document>(plain, Json);
            if (doc is null || doc.Profiles is null)
            {
                throw new InvalidDataException("The export is empty or damaged.");
            }

            return doc.Version == Version ? doc.Profiles : throw new InvalidDataException($"Unsupported export version {doc.Version}.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The export is damaged.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}
