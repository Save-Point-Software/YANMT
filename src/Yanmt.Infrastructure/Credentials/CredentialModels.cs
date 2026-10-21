using System.ComponentModel.DataAnnotations;
using Yanmt.Core.Credentials;

namespace Yanmt.Infrastructure.Credentials;

public sealed record CredentialListItem(
    int Id,
    string Name,
    CredentialKind Kind,
    string Username,
    int Port,
    SnmpAuthProtocol? SnmpAuth,
    SnmpPrivProtocol? SnmpPriv,
    int NodeCount,
    DateTime UpdatedUtc)
{
    public string Description => Kind == CredentialKind.Ssh
        ? $"SSH, port {Port}"
        : $"SNMPv3 authPriv {CredentialRules.Label(SnmpAuth!.Value)}/{CredentialRules.Label(SnmpPriv!.Value)}, port {Port}";
}

/// <summary>A profile for a picker. Note flags legacy SHA-1 or a privacy protocol not verified on IOS-XE.</summary>
public sealed record CredentialOption(int Id, string Name, string? Note = null)
{
    public string Display => Note is null ? Name : $"{Name} ({Note})";

    internal static string? NoteFor(SnmpAuthProtocol? auth, SnmpPrivProtocol? priv) =>
        auth is { } a && CredentialRules.IsLegacy(a) ? $"{CredentialRules.Describe(a)}, legacy"
        : priv is { } p && !CredentialRules.IsVerifiedOnIosXe(p) ? $"{CredentialRules.Describe(p)}, not verified on IOS-XE"
        : null;
}

public sealed record DeleteProfileResult(bool Ok, IReadOnlyList<string> UsedBy);

/// <summary>
/// Form model for a credential profile. The secret fields are write-only: they are always empty
/// when a profile is loaded for editing, and blank on save means "keep the stored value".
/// </summary>
public sealed class CredentialEditModel : IValidatableObject
{
    /// <summary>Set by the form, not bound: secrets are required only when creating.</summary>
    public bool IsNew { get; set; } = true;

    /// <summary>Set when loading: false if the stored secrets can't be decrypted on this server.</summary>
    public bool StoredSecretsReadable { get; set; } = true;

    public string Name { get; set; } = "";
    public CredentialKind Kind { get; set; } = CredentialKind.SnmpV3;
    public string Username { get; set; } = "";
    public int Port { get; set; } = CredentialRules.DefaultSnmpPort;
    public SnmpAuthProtocol SnmpAuth { get; set; } = SnmpAuthProtocol.Sha256;
    public SnmpPrivProtocol SnmpPriv { get; set; } = SnmpPrivProtocol.Aes128;

    public string? Password { get; set; }
    public string? AuthPassword { get; set; }
    public string? PrivPassword { get; set; }

    public void ClearSecrets()
    {
        Password = null;
        AuthPassword = null;
        PrivPassword = null;
    }

    /// <summary>True when every secret this kind needs must be supplied (new profile, or stored ones unreadable).</summary>
    public bool SecretsRequired => IsNew || !StoredSecretsReadable;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var (error, member) in new[]
        {
            (CredentialRules.ValidateName(Name), nameof(Name)),
            (CredentialRules.ValidateUsername(Username), nameof(Username)),
            (CredentialRules.ValidatePort(Port), nameof(Port)),
        })
        {
            if (error is not null)
            {
                yield return new ValidationResult(error, [member]);
            }
        }

        if (Kind == CredentialKind.SnmpV3 && CredentialRules.ValidateSnmpProtocols(SnmpAuth, SnmpPriv) is { } pairing)
        {
            yield return new ValidationResult(pairing, [nameof(SnmpPriv)]);
        }

        var secrets = Kind == CredentialKind.Ssh
            ? new[] { ("Password", Password, nameof(Password)) }
            : new[] { ("Auth password", AuthPassword, nameof(AuthPassword)), ("Privacy password", PrivPassword, nameof(PrivPassword)) };
        foreach (var (label, value, member) in secrets)
        {
            if (string.IsNullOrEmpty(value))
            {
                if (SecretsRequired)
                {
                    yield return new ValidationResult($"{label} is required.", [member]);
                }
            }
            else if (CredentialRules.ValidateSecret(label, value, Kind) is { } error)
            {
                yield return new ValidationResult(error, [member]);
            }
        }
    }
}
