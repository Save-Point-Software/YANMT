using System.Security.Cryptography;
using System.Text;
using Yanmt.Core.Status;

namespace Yanmt.Core.ConfigBackups;

/// <summary>What a stored version means relative to the latest one.</summary>
public enum ConfigChangeKind
{
    /// <summary>Same normalized content as the latest version: store nothing.</summary>
    Unchanged,

    /// <summary>No earlier version: store it, but it isn't a "change" event.</summary>
    Initial,

    /// <summary>Normalized content differs: store a version and raise a config-change event.</summary>
    Changed,
}

public sealed record BackupEvaluation(Severity State, string ValueText, string Reason);

/// <summary>Pure backup logic: content hashing, change detection, and the backup source's state.</summary>
public static class ConfigBackupEvaluator
{
    /// <summary>SHA-256 (lowercase hex) of the normalized text, UTF-8. Computed before encryption.</summary>
    public static string Hash(string normalizedConfig)
    {
        ArgumentNullException.ThrowIfNull(normalizedConfig);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedConfig)));
    }

    public static ConfigChangeKind Classify(string? latestHash, string newHash) =>
        latestHash is null ? ConfigChangeKind.Initial
        : string.Equals(latestHash, newHash, StringComparison.Ordinal) ? ConfigChangeKind.Unchanged
        : ConfigChangeKind.Changed;

    /// <summary>A successful pull is Normal; a failed one is Warning (never Critical, never silent).</summary>
    public static BackupEvaluation EvaluateSuccess(ConfigChangeKind kind) => kind switch
    {
        ConfigChangeKind.Initial => new(Severity.Normal, "first backup", "first config version stored"),
        ConfigChangeKind.Changed => new(Severity.Normal, "config changed", "config changed; new version stored"),
        _ => new(Severity.Normal, "no change", "config unchanged since the last backup"),
    };

    public static BackupEvaluation EvaluateFailure(string reason) =>
        new(Severity.Warning, "backup failed", string.IsNullOrWhiteSpace(reason) ? "backup failed" : reason.Trim());
}
