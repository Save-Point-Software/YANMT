using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yanmt.Core.ConfigBackups;
using Yanmt.Core.Connectivity;
using Yanmt.Core.Ssh;
using Yanmt.Infrastructure.Collectors.Ssh;
using Yanmt.Infrastructure.Credentials;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Data.Entities;
using Yanmt.Infrastructure.Monitoring;
using Yanmt.Infrastructure.Security;
using Yanmt.Infrastructure.Settings;

namespace Yanmt.Infrastructure.ConfigBackups;

/// <summary>Who started a backup. Only a scheduled backup's failure changes the node's status.</summary>
public enum BackupTrigger
{
    /// <summary>The daily (or configured-interval) run. A failure sets the Backup source to Warning, so the node changes colour.</summary>
    Scheduled,

    /// <summary>An operator pressed "Back up now". A failure is shown to them, but status and events stay untouched.</summary>
    Manual,
}

/// <summary>What one backup attempt did.</summary>
public sealed record BackupOutcome(bool Success, ConfigChangeKind? Kind, string Message, FailureCategory? Failure = null)
{
    public static BackupOutcome Failed(FailureCategory category, string message) => new(false, null, message, category);
}

/// <param name="LatestCapturedUtc">When the newest version was stored (the last backup that found a change, or the first one).</param>
/// <param name="LastVerifiedUtc">When a backup, scheduled or manual, last fetched the config and compared it, even if nothing changed.</param>
public sealed record ConfigBackupSummary(int VersionCount, DateTime? LatestCapturedUtc, DateTime? LastVerifiedUtc);

/// <summary>An outcome for the caller, plus the status result to hand to the engine (null when nothing should change status).</summary>
public sealed record BackupAttempt(BackupOutcome Outcome, BackupPollResult? Result);

/// <summary>
/// Pulls <c>show running-config</c> from a node over SSH and keeps a new <see cref="ConfigVersion"/>
/// only when the normalized text changed. The only command sent is the allowlisted
/// <see cref="CommandAllowlist.ShowRunningConfig"/>, through <see cref="ReadOnlySshCollector"/>.
/// This service stores versions; it never writes status. Every attempt is handed to
/// <see cref="StatusEngine"/> as a <see cref="BackupPollResult"/>, the single writer of state and events.
/// </summary>
public sealed class ConfigBackupService(
    IDbContextFactory<YanmtDbContext> dbFactory,
    CredentialService credentials,
    ReadOnlySshCollector ssh,
    SecretProtector protector,
    SettingsService settings,
    StatusEngine engine,
    TimeProvider clock,
    ILogger<ConfigBackupService> logger)
{
    internal static readonly TimeSpan SshConnectTimeout = TimeSpan.FromSeconds(15);

    // A full running-config can take a while to print on a big router.
    internal static readonly TimeSpan SshCommandTimeout = TimeSpan.FromSeconds(120);

    private static readonly ConcurrentDictionary<int, SemaphoreSlim> NodeLocks = new();

    /// <summary>
    /// Backs up one node and submits the result to the status engine. For callers with their own
    /// submit path (the scheduler's <c>NodePoller</c>), use <see cref="AttemptAsync"/> instead.
    /// Failures come back as a failed outcome; only cancellation throws. A failed
    /// <see cref="BackupTrigger.Scheduled"/> backup also sets the node's backup source to Warning, while a
    /// failed <see cref="BackupTrigger.Manual"/> one doesn't (the operator sees the outcome).
    /// </summary>
    public async Task<BackupOutcome> BackupNodeAsync(int nodeId, BackupTrigger trigger, CancellationToken cancellationToken = default)
    {
        var attempt = await AttemptAsync(nodeId, trigger, cancellationToken);
        if (attempt.Result is { } result)
        {
            await engine.SubmitAsync(result, cancellationToken);
        }

        return attempt.Outcome;
    }

    /// <summary>
    /// Runs one backup (fetch, normalize, store a version if changed) without touching status: the
    /// caller submits <see cref="BackupAttempt.Result"/>. Failures are returned, never thrown, except cancellation.
    /// </summary>
    public async Task<BackupAttempt> AttemptAsync(int nodeId, BackupTrigger trigger, CancellationToken cancellationToken = default)
    {
        // Two overlapping backups of one node could both see "no earlier version" and store twice.
        var gate = NodeLocks.GetOrAdd(nodeId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            Node? node;
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
            {
                node = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == nodeId, cancellationToken);
            }

            if (node is null)
            {
                return new BackupAttempt(BackupOutcome.Failed(FailureCategory.NotConfigured, "This node no longer exists."), null);
            }

            var fetched = await FetchAsync(node, cancellationToken);
            return fetched.Raw is { } raw
                ? await RecordAsync(node.Id, raw, trigger, cancellationToken)
                : Fail(node.Id, trigger, fetched.Failure!.Value, fetched.Error!);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Normalizes and hashes <paramref name="raw"/>, stores a version if the content changed, and reports
    /// the attempt to the status engine. A bad normalization pattern fails the backup visibly.
    /// </summary>
    internal async Task<BackupAttempt> RecordAsync(int nodeId, string raw, BackupTrigger trigger, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        string normalized;
        try
        {
            var config = await settings.GetAsync(SettingKeys.ConfigBackup, cancellationToken);
            normalized = ConfigNormalizer.Normalize(raw, config.NormalizationPatterns);
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return Fail(nodeId, trigger, FailureCategory.NotConfigured, $"A config ignore pattern is invalid or too slow: {ex.Message}");
        }

        var hash = ConfigBackupEvaluator.Hash(normalized);
        ConfigChangeKind kind;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var latest = await db.ConfigVersions.AsNoTracking()
                .Where(v => v.NodeId == nodeId)
                .OrderByDescending(v => v.CapturedUtc).ThenByDescending(v => v.Id)
                .Select(v => v.ContentHash)
                .FirstOrDefaultAsync(cancellationToken);
            kind = ConfigBackupEvaluator.Classify(latest, hash);

            if (kind != ConfigChangeKind.Unchanged)
            {
                db.ConfigVersions.Add(new ConfigVersion
                {
                    NodeId = nodeId,
                    CapturedUtc = now,
                    ContentHash = hash,
                    ProtectedRaw = protector.Protect(Encoding.UTF8.GetBytes(raw)),
                    RawLength = raw.Length,
                    LineCount = raw.AsSpan().Count('\n') + 1,
                });
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        return new BackupAttempt(
            new BackupOutcome(true, kind, ConfigBackupEvaluator.EvaluateSuccess(kind).Reason),
            new BackupPollResult(nodeId, now, kind));
    }

    /// <summary>Version count and the newest capture time for a node. No config text.</summary>
    public async Task<ConfigBackupSummary> GetSummaryAsync(int nodeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var versions = db.ConfigVersions.AsNoTracking().Where(v => v.NodeId == nodeId);
        var count = await versions.CountAsync(cancellationToken);
        var latest = count == 0 ? null : (DateTime?)await versions.MaxAsync(v => v.CapturedUtc, cancellationToken);
        var verified = await db.StatusSources.AsNoTracking()
            .Where(x => x.NodeId == nodeId && x.SourceType == StatusSourceType.Backup && x.SourceKey == "")
            .Select(x => x.LastVerifiedUtc)
            .FirstOrDefaultAsync(cancellationToken);
        return new ConfigBackupSummary(count, latest, verified);
    }

    /// <summary>Returns the raw text of a stored version, decrypted. Callers must mask it for viewers.</summary>
    /// <exception cref="SecretUnreadableException">The version can't be decrypted on this server.</exception>
    internal async Task<string?> ReadRawAsync(long versionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var blob = await db.ConfigVersions.AsNoTracking()
            .Where(v => v.Id == versionId)
            .Select(v => v.ProtectedRaw)
            .FirstOrDefaultAsync(cancellationToken);
        return blob is null ? null : Encoding.UTF8.GetString(protector.Unprotect(blob));
    }

    private BackupAttempt Fail(int nodeId, BackupTrigger trigger, FailureCategory category, string message)
    {
        logger.LogWarning("Config backup of node {NodeId} failed ({Trigger}, {Category}): {Message}", nodeId, trigger, category, message);
        var outcome = BackupOutcome.Failed(category, message);

        // Only a scheduled failure changes status; a manual one is shown to the operator who asked.
        return new BackupAttempt(outcome, trigger == BackupTrigger.Scheduled
            ? new BackupPollResult(nodeId, clock.GetUtcNow().UtcDateTime, null, message)
            : null);
    }

    private async Task<(string? Raw, FailureCategory? Failure, string? Error)> FetchAsync(Node node, CancellationToken cancellationToken)
    {
        if (node.SshProfileId is not { } profileId)
        {
            return (null, FailureCategory.NotConfigured, "No SSH profile is assigned to this node.");
        }

        if (!node.SshEnabled)
        {
            return (null, FailureCategory.NotConfigured, "SSH is switched off for this node.");
        }

        try
        {
            var credential = await credentials.LoadSshAsync(profileId, cancellationToken);
            int? port;
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
            {
                port = await db.CredentialProfiles.Where(p => p.Id == profileId).Select(p => (int?)p.Port).FirstOrDefaultAsync(cancellationToken);
            }

            if (credential is null || port is null)
            {
                return (null, FailureCategory.NotConfigured, "The assigned SSH profile no longer exists.");
            }

            // A pinned key must match; an unpinned node trusts on first use and is pinned below.
            var target = new SshTarget(node.ManagementIp, port.Value, credential, node.SshHostKeyFingerprint, SshConnectTimeout, SshCommandTimeout);
            var run = await ssh.RunAsync(target, [CommandAllowlist.ShowRunningConfig], cancellationToken);

            if (node.SshHostKeyFingerprint is null)
            {
                await PinHostKeyAsync(node.Id, run.HostKey, cancellationToken);
            }

            var output = run.Outputs[0];
            if (output.ExitStatus is { } status and not 0)
            {
                return (null, FailureCategory.DeviceError, $"'{output.Command}' returned exit status {status}. Check that the account is privilege 15.");
            }

            return ConfigOutputCheck.Problem(output.Output) is { } problem
                ? (null, FailureCategory.DeviceError, problem)
                : (output.Output, null, null);
        }
        catch (SshCollectorException ex)
        {
            return (null, ex.Category, ex.Message);
        }
        catch (SecretUnreadableException ex)
        {
            return (null, FailureCategory.CredentialUnreadable, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Config backup of node {NodeId} hit an unexpected error", node.Id);
            return (null, FailureCategory.Other, $"Unexpected error: {ex.Message}");
        }
    }

    private async Task PinHostKeyAsync(int nodeId, SshHostKey key, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var node = await db.Nodes.FirstOrDefaultAsync(n => n.Id == nodeId, cancellationToken);
        if (node is { SshHostKeyFingerprint: null })
        {
            node.SshHostKeyAlgorithm = key.Algorithm;
            node.SshHostKeyFingerprint = key.Fingerprint;
            await db.SaveChangesAsync(cancellationToken); // audited: the first connection pinned this key
        }
    }
}

/// <summary>Sanity checks on a <c>show running-config</c> capture, so an error banner is never stored as a config.</summary>
internal static class ConfigOutputCheck
{
    /// <summary>Null when the output looks like a complete IOS-XE running-config; otherwise what is wrong.</summary>
    public static string? Problem(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return "The device returned no output for the running-config.";
        }

        var lines = output.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        if (lines[0].StartsWith('%'))
        {
            return $"The device refused the command: {lines[0]}";
        }

        // IOS prints "end" last. Without it the output was cut short, and storing it would look like a change.
        return lines[^1] == "end" ? null : "The running-config output doesn't end with 'end' (cut short?). Nothing was stored.";
    }
}
