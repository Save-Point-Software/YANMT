using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Yanmt.Core.ConfigBackups;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Security;
using Yanmt.Infrastructure.Settings;

namespace Yanmt.Infrastructure.ConfigBackups;

/// <summary>Version metadata for the history list. No config text.</summary>
public sealed record ConfigVersionInfo(long Id, DateTime CapturedUtc, int RawLength, int LineCount, string ContentHash);

/// <summary>Config text ready for the browser. <see cref="Revealed"/> is true only when secrets are included.</summary>
public sealed record ConfigText(string Text, bool Revealed);

/// <summary>A diff of two versions, in both layouts. <see cref="Revealed"/> as for <see cref="ConfigText"/>.</summary>
public sealed record ConfigDiffResult(
    ConfigVersionInfo Old, ConfigVersionInfo New, bool Revealed, bool Normalized,
    IReadOnlyList<SideBySideRow> SideBySide, IReadOnlyList<DiffLine> Unified);

/// <summary>
/// Read side of config history: lists versions and returns text, diffs and zips. This is the only place
/// decrypted config leaves the server, so the masking rule lives here: unmasked text is returned only when
/// the caller asks for it <b>and</b> the principal is an admin, and every such reveal is audit-logged.
/// A viewer asking to reveal silently gets masked text.
/// </summary>
public sealed class ConfigHistoryService(
    IDbContextFactory<YanmtDbContext> dbFactory,
    ConfigBackupService backups,
    SettingsService settings,
    AuditLogService audit)
{
    public async Task<List<ConfigVersionInfo>> ListAsync(int nodeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.ConfigVersions.AsNoTracking()
            .Where(v => v.NodeId == nodeId)
            .OrderByDescending(v => v.CapturedUtc).ThenByDescending(v => v.Id)
            .Select(v => new ConfigVersionInfo(v.Id, v.CapturedUtc, v.RawLength, v.LineCount, v.ContentHash))
            .ToListAsync(cancellationToken);
    }

    /// <summary>The raw text of one version, masked unless an admin reveals it. Null if the version isn't this node's.</summary>
    public async Task<ConfigText?> GetTextAsync(int nodeId, long versionId, ClaimsPrincipal user, bool reveal, CancellationToken cancellationToken = default)
    {
        if (await FindAsync(nodeId, versionId, cancellationToken) is null || await backups.ReadRawAsync(versionId, cancellationToken) is not { } raw)
        {
            return null;
        }

        var revealed = reveal && IsAdmin(user);
        if (revealed)
        {
            await AuditAsync("RevealConfig", nodeId, new { versionIds = new[] { versionId } }, cancellationToken);
            return new ConfigText(raw, true);
        }

        return new ConfigText(ConfigSecretMasker.MaskSecrets(raw), false);
    }

    /// <summary>
    /// Diffs two versions of a node (oldest-first is the caller's choice). The comparison runs on the normalized
    /// text, so volatile lines don't show up as changes; if a pattern is invalid it falls back to the raw text.
    /// </summary>
    public async Task<ConfigDiffResult?> DiffAsync(int nodeId, long oldId, long newId, ClaimsPrincipal user, bool reveal, CancellationToken cancellationToken = default)
    {
        var oldInfo = await FindAsync(nodeId, oldId, cancellationToken);
        var newInfo = await FindAsync(nodeId, newId, cancellationToken);
        if (oldInfo is null || newInfo is null
            || await backups.ReadRawAsync(oldId, cancellationToken) is not { } oldRaw
            || await backups.ReadRawAsync(newId, cancellationToken) is not { } newRaw)
        {
            return null;
        }

        var (oldText, normalized) = await NormalizeAsync(oldRaw, cancellationToken);
        var (newText, _) = await NormalizeAsync(newRaw, cancellationToken);
        var revealed = reveal && IsAdmin(user);
        if (revealed)
        {
            await AuditAsync("RevealConfig", nodeId, new { versionIds = new[] { oldId, newId }, view = "diff" }, cancellationToken);
        }
        else
        {
            oldText = ConfigSecretMasker.MaskSecrets(oldText);
            newText = ConfigSecretMasker.MaskSecrets(newText);
        }

        return new ConfigDiffResult(oldInfo, newInfo, revealed, normalized,
            ConfigDiffBuilder.SideBySide(oldText, newText), ConfigDiffBuilder.Unified(oldText, newText));
    }

    /// <summary>
    /// Writes a zip of every version of a node to <paramref name="output"/>, one <c>.txt</c> per version.
    /// Masked unless <paramref name="includeSecrets"/> is set by an admin; that opt-in is audit-logged.
    /// Returns false (nothing written) if the node has no versions.
    /// </summary>
    public async Task<bool> WriteZipAsync(int nodeId, string nodeName, ClaimsPrincipal user, bool includeSecrets, Stream output, CancellationToken cancellationToken = default)
    {
        var versions = await ListAsync(nodeId, cancellationToken);
        if (versions.Count == 0)
        {
            return false;
        }

        var withSecrets = includeSecrets && IsAdmin(user);
        if (withSecrets)
        {
            await AuditAsync("ExportConfigWithSecrets", nodeId, new { versionCount = versions.Count }, cancellationToken);
        }

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var v in versions)
        {
            if (await backups.ReadRawAsync(v.Id, cancellationToken) is not { } raw)
            {
                continue;
            }

            var entry = zip.CreateEntry($"{SafeName(nodeName)}/{SafeName(nodeName)}-{v.CapturedUtc:yyyyMMdd-HHmmss}-v{v.Id}.txt", CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(DateTime.SpecifyKind(v.CapturedUtc, DateTimeKind.Utc));
            await using var stream = entry.Open();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(withSecrets ? raw : ConfigSecretMasker.MaskSecrets(raw)), cancellationToken);
        }

        return true;
    }

    /// <summary>File-name-safe version of a node name or label (letters, digits, dot, dash, underscore).</summary>
    public static string SafeName(string name)
    {
        var cleaned = Regex.Replace(name, @"[^A-Za-z0-9._-]+", "_").Trim('_', '.');
        return cleaned.Length == 0 ? "node" : cleaned;
    }

    private static bool IsAdmin(ClaimsPrincipal user) => user.IsInRole(YanmtRoles.Admin);

    private async Task<ConfigVersionInfo?> FindAsync(int nodeId, long versionId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.ConfigVersions.AsNoTracking()
            .Where(v => v.Id == versionId && v.NodeId == nodeId)
            .Select(v => new ConfigVersionInfo(v.Id, v.CapturedUtc, v.RawLength, v.LineCount, v.ContentHash))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<(string Text, bool Normalized)> NormalizeAsync(string raw, CancellationToken cancellationToken)
    {
        try
        {
            var config = await settings.GetAsync(SettingKeys.ConfigBackup, cancellationToken);
            return (ConfigNormalizer.Normalize(raw, config.NormalizationPatterns), true);
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return (raw, false);
        }
    }

    // Records who and which versions, never any config text.
    private Task AuditAsync(string action, int nodeId, object detail, CancellationToken cancellationToken) =>
        audit.RecordAsync(action, "ConfigVersion", $"node:{nodeId}", JsonSerializer.Serialize(detail), cancellationToken);
}
