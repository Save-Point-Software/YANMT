using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yanmt.Core.Credentials;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Data.Entities;
using Yanmt.Infrastructure.Inventory;
using Yanmt.Infrastructure.Security;

namespace Yanmt.Infrastructure.Credentials;

/// <summary>
/// Credential profiles. Secrets go in encrypted and never come back out through the public API:
/// only the collectors' callers in this assembly can decrypt (<see cref="LoadSshAsync"/>,
/// <see cref="LoadSnmpAsync"/>). Saves are audited, with the secret blob shown as "***".
/// </summary>
public sealed class CredentialService(IDbContextFactory<YanmtDbContext> dbFactory, SecretProtector protector, TimeProvider clock)
{
    public async Task<List<CredentialListItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var profiles = await db.CredentialProfiles.AsNoTracking().OrderBy(p => p.Name).ToListAsync(cancellationToken);
        var usage = await UsageCountsAsync(db, cancellationToken);
        return profiles
            .Select(p => new CredentialListItem(p.Id, p.Name, p.Kind, p.Username, p.Port, p.SnmpAuth, p.SnmpPriv, usage.GetValueOrDefault(p.Id), p.UpdatedUtc))
            .ToList();
    }

    public async Task<List<CredentialOption>> OptionsAsync(CredentialKind kind, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var profiles = await db.CredentialProfiles.AsNoTracking()
            .Where(p => p.Kind == kind)
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Name, p.SnmpAuth, p.SnmpPriv })
            .ToListAsync(cancellationToken);
        return profiles.Select(p => new CredentialOption(p.Id, p.Name, CredentialOption.NoteFor(p.SnmpAuth, p.SnmpPriv))).ToList();
    }

    /// <summary>The profile's non-secret fields. Secret fields are left empty.</summary>
    public async Task<CredentialEditModel?> GetForEditAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var p = await db.CredentialProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (p is null)
        {
            return null;
        }

        return new CredentialEditModel
        {
            IsNew = false,
            StoredSecretsReadable = TryReadSecrets(p.ProtectedSecrets, out _),
            Name = p.Name,
            Kind = p.Kind,
            Username = p.Username,
            Port = p.Port,
            SnmpAuth = p.SnmpAuth ?? SnmpAuthProtocol.Sha256,
            SnmpPriv = p.SnmpPriv ?? SnmpPrivProtocol.Aes128,
        };
    }

    public Task<SaveResult> CreateAsync(CredentialEditModel model, CancellationToken cancellationToken = default) =>
        SaveAsync(null, model, cancellationToken);

    public Task<SaveResult> UpdateAsync(int id, CredentialEditModel model, CancellationToken cancellationToken = default) =>
        SaveAsync(id, model, cancellationToken);

    public async Task<DeleteProfileResult> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var usedBy = await db.Nodes.AsNoTracking()
            .Where(n => n.SnmpProfileId == id || n.SshProfileId == id)
            .OrderBy(n => n.Hostname)
            .Select(n => n.Hostname)
            .ToListAsync(cancellationToken);
        if (usedBy.Count > 0)
        {
            return new DeleteProfileResult(false, usedBy);
        }

        var profile = await db.CredentialProfiles.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (profile is not null)
        {
            db.CredentialProfiles.Remove(profile);
            await db.SaveChangesAsync(cancellationToken);
        }

        return new DeleteProfileResult(true, []);
    }

    /// <exception cref="SecretUnreadableException">Stored secrets can't be decrypted here.</exception>
    internal async Task<SshCredential?> LoadSshAsync(int profileId, CancellationToken cancellationToken = default)
    {
        var p = await FindAsync(profileId, CredentialKind.Ssh, cancellationToken);
        if (p is null)
        {
            return null;
        }

        var secrets = ReadSecrets(p.ProtectedSecrets);
        return new SshCredential(p.Username, secrets.Password ?? "");
    }

    /// <exception cref="SecretUnreadableException">Stored secrets can't be decrypted here.</exception>
    internal async Task<(SnmpV3Credential Credential, int Port)?> LoadSnmpAsync(int profileId, CancellationToken cancellationToken = default)
    {
        var p = await FindAsync(profileId, CredentialKind.SnmpV3, cancellationToken);
        if (p is null)
        {
            return null;
        }

        var secrets = ReadSecrets(p.ProtectedSecrets);
        return (new SnmpV3Credential(p.Username, p.SnmpAuth!.Value, secrets.AuthPassword ?? "", p.SnmpPriv!.Value, secrets.PrivPassword ?? ""), p.Port);
    }

    private async Task<CredentialProfile?> FindAsync(int id, CredentialKind kind, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CredentialProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.Kind == kind, cancellationToken);
    }

    private async Task<SaveResult> SaveAsync(int? id, CredentialEditModel model, CancellationToken cancellationToken)
    {
        model.IsNew = id is null;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        CredentialProfile? profile = null;
        SecretBag? stored = null;
        if (id is not null)
        {
            profile = await db.CredentialProfiles.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
            if (profile is null)
            {
                return SaveResult.Fail(null, "This profile no longer exists.");
            }

            if (profile.Kind != model.Kind)
            {
                return SaveResult.Fail(nameof(model.Kind), "A profile's type can't be changed. Create a new profile instead.");
            }

            model.StoredSecretsReadable = TryReadSecrets(profile.ProtectedSecrets, out stored);
        }

        // Re-validate on the server: the form's validation is a convenience, not the boundary.
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(model, new ValidationContext(model), errors, validateAllProperties: true))
        {
            var first = errors[0];
            return SaveResult.Fail(first.MemberNames.FirstOrDefault(), first.ErrorMessage ?? "Invalid profile.");
        }

        var name = model.Name.Trim();
        if (await db.CredentialProfiles.AnyAsync(p => p.Name == name && p.Id != id, cancellationToken))
        {
            return SaveResult.Fail(nameof(model.Name), $"A profile named '{name}' already exists.");
        }

        // Blank secret fields keep the stored values.
        var secrets = model.Kind == CredentialKind.Ssh
            ? new SecretBag(Pick(model.Password, stored?.Password), null, null)
            : new SecretBag(null, Pick(model.AuthPassword, stored?.AuthPassword), Pick(model.PrivPassword, stored?.PrivPassword));
        var secretsChanged = id is null || !string.IsNullOrEmpty(model.Password) || !string.IsNullOrEmpty(model.AuthPassword) || !string.IsNullOrEmpty(model.PrivPassword);

        var now = clock.GetUtcNow().UtcDateTime;
        if (profile is null)
        {
            profile = new CredentialProfile { Name = name, Username = model.Username, ProtectedSecrets = [], Kind = model.Kind, CreatedUtc = now };
            db.CredentialProfiles.Add(profile);
        }

        profile.Name = name;
        profile.Username = model.Username.Trim();
        profile.Port = model.Port;
        profile.SnmpAuth = model.Kind == CredentialKind.SnmpV3 ? model.SnmpAuth : null;
        profile.SnmpPriv = model.Kind == CredentialKind.SnmpV3 ? model.SnmpPriv : null;
        if (secretsChanged)
        {
            profile.ProtectedSecrets = Protect(secrets);
        }

        profile.UpdatedUtc = now;
        model.ClearSecrets();

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            return SaveResult.Fail(nameof(model.Name), $"A profile named '{name}' already exists.");
        }

        return SaveResult.Success(profile.Id);

        static string? Pick(string? entered, string? kept) => string.IsNullOrEmpty(entered) ? kept : entered;
    }

    private static async Task<Dictionary<int, int>> UsageCountsAsync(YanmtDbContext db, CancellationToken cancellationToken)
    {
        var refs = await db.Nodes.AsNoTracking()
            .Select(n => new { n.SnmpProfileId, n.SshProfileId })
            .ToListAsync(cancellationToken);
        return refs
            .SelectMany(r => new[] { r.SnmpProfileId, r.SshProfileId })
            .Where(x => x is not null)
            .GroupBy(x => x!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    private byte[] Protect(SecretBag secrets) => protector.Protect(JsonSerializer.SerializeToUtf8Bytes(secrets));

    private SecretBag ReadSecrets(byte[] blob)
    {
        var json = protector.Unprotect(blob);
        try
        {
            return JsonSerializer.Deserialize<SecretBag>(json) ?? throw new SecretUnreadableException("The stored credential is empty.");
        }
        catch (JsonException ex)
        {
            throw new SecretUnreadableException("The stored credential is corrupt.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    private bool TryReadSecrets(byte[] blob, out SecretBag? secrets)
    {
        try
        {
            secrets = ReadSecrets(blob);
            return true;
        }
        catch (SecretUnreadableException)
        {
            secrets = null;
            return false;
        }
    }

    /// <summary>The plaintext document inside the DPAPI blob. Fields not used by a profile's kind are null.</summary>
    private sealed record SecretBag(string? Password, string? AuthPassword, string? PrivPassword)
    {
        public override string ToString() => "SecretBag(***)";
    }
}
