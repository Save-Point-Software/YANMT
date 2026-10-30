using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yanmt.Core.Credentials;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Data.Entities;
using Yanmt.Infrastructure.Monitoring;
using Yanmt.Infrastructure.Security;

namespace Yanmt.Infrastructure.Credentials;

public enum ImportConflict
{
    /// <summary>A profile whose name already exists is left alone.</summary>
    Skip,

    /// <summary>A profile whose name already exists (same type) gets the file's values.</summary>
    Overwrite,
}

public sealed record CredentialExportResult(byte[] File, int ProfileCount, IReadOnlyList<string> Unreadable);

public sealed record CredentialImportResult(
    bool Ok, string? Error, int Created, int Updated, IReadOnlyList<string> Skipped, IReadOnlyList<string> Rejected);

/// <summary>
/// Admin-only passphrase export and import of credential profiles, for moving to another server (DPAPI
/// blobs don't survive that). The file holds secrets, so: the caller's role is checked here rather than
/// trusted from the UI, the passphrase and plaintext are never logged or audited, and the audit entry
/// records only who, when, and counts.
/// </summary>
public sealed class CredentialTransferService(
    IDbContextFactory<YanmtDbContext> dbFactory, CredentialService credentials, AuditLogService audit, TimeProvider clock, InventoryChangeSignal? signal = null)
{
    public async Task<CredentialExportResult> ExportAsync(ClaimsPrincipal user, string passphrase, int iterations = PassphraseCrypto.DefaultIterations, CancellationToken cancellationToken = default)
    {
        RequireAdmin(user);
        if (PassphraseCrypto.ValidatePassphrase(passphrase) is { } error)
        {
            throw new ArgumentException(error, nameof(passphrase));
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.CredentialProfiles.AsNoTracking().OrderBy(p => p.Name).ToListAsync(cancellationToken);
        var archived = new List<ArchivedProfile>();
        var unreadable = new List<string>();
        foreach (var p in rows)
        {
            if (credentials.TryReadPlain(p.ProtectedSecrets) is not { } s)
            {
                unreadable.Add(p.Name);
                continue;
            }

            archived.Add(new ArchivedProfile(p.Name, p.Kind, p.Username, p.Port, p.SnmpAuth, p.SnmpPriv, s.Password, s.AuthPassword, s.PrivPassword));
        }

        var file = CredentialArchive.Seal(archived, passphrase, iterations);
        await audit.RecordAsync("ExportCredentials", nameof(CredentialProfile), null,
            JsonSerializer.Serialize(new { profiles = archived.Count, unreadable = unreadable.Count }), cancellationToken);
        return new CredentialExportResult(file, archived.Count, unreadable);
    }

    public async Task<CredentialImportResult> ImportAsync(
        ClaimsPrincipal user, byte[] file, string passphrase, ImportConflict conflict, CancellationToken cancellationToken = default)
    {
        RequireAdmin(user);

        IReadOnlyList<ArchivedProfile>? profiles;
        try
        {
            profiles = CredentialArchive.Open(file, passphrase);
        }
        catch (InvalidDataException ex)
        {
            await AuditFailureAsync(cancellationToken);
            return Fail(ex.Message);
        }

        if (profiles is null)
        {
            await AuditFailureAsync(cancellationToken);
            return Fail("Wrong passphrase, or the file has been modified.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.CredentialProfiles.ToDictionaryAsync(p => p.Name, cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        int created = 0, updated = 0;
        var skipped = new List<string>();
        var rejected = new List<string>();
        var seen = new HashSet<string>();

        foreach (var a in profiles)
        {
            var label = a.Name ?? "(unnamed)";
            if (a.Name is null || a.Username is null || a.Validate() is { } problem)
            {
                rejected.Add($"{label}: {(a.Name is null || a.Username is null ? "Name or username is missing." : a.Validate())}");
                continue;
            }

            var name = a.Name.Trim();
            if (!seen.Add(name))
            {
                rejected.Add($"{name}: duplicate name in the file.");
                continue;
            }

            var isSsh = a.Kind == CredentialKind.Ssh;
            if (existing.TryGetValue(name, out var profile))
            {
                if (conflict == ImportConflict.Skip)
                {
                    skipped.Add($"{name}: already exists.");
                    continue;
                }

                if (profile.Kind != a.Kind)
                {
                    rejected.Add($"{name}: exists with a different type; not overwritten.");
                    continue;
                }

                updated++;
            }
            else
            {
                profile = new CredentialProfile { Name = name, Username = a.Username, ProtectedSecrets = [], Kind = a.Kind, CreatedUtc = now };
                db.CredentialProfiles.Add(profile);
                created++;
            }

            profile.Username = a.Username.Trim();
            profile.Port = a.Port;
            profile.SnmpAuth = isSsh ? null : a.SnmpAuth;
            profile.SnmpPriv = isSsh ? null : a.SnmpPriv;
            profile.ProtectedSecrets = isSsh
                ? credentials.ProtectPlain(a.Password, null, null)
                : credentials.ProtectPlain(null, a.AuthPassword, a.PrivPassword);
            profile.UpdatedUtc = now;
        }

        if (created + updated > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            signal?.Notify();
        }

        await audit.RecordAsync("ImportCredentials", nameof(CredentialProfile), null,
            JsonSerializer.Serialize(new { created, updated, skipped = skipped.Count, rejected = rejected.Count, overwrite = conflict == ImportConflict.Overwrite }), cancellationToken);
        return new CredentialImportResult(true, null, created, updated, skipped, rejected);
    }

    private Task AuditFailureAsync(CancellationToken cancellationToken) =>
        audit.RecordAsync("ImportCredentialsFailed", nameof(CredentialProfile), null, null, cancellationToken);

    private static CredentialImportResult Fail(string error) => new(false, error, 0, 0, [], []);

    private static void RequireAdmin(ClaimsPrincipal user)
    {
        if (!user.IsInRole(YanmtRoles.Admin))
        {
            throw new UnauthorizedAccessException("Credential export and import are admin-only.");
        }
    }
}
