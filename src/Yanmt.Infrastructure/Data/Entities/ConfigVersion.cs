namespace Yanmt.Infrastructure.Data.Entities;

/// <summary>
/// One stored <c>show running-config</c> capture of a node. Runtime data written by
/// <c>ConfigBackupService</c> (not <see cref="IAudited"/>: the audit log must never see config text).
/// The raw config is DPAPI-encrypted (it contains secrets) and is never masked or altered when stored.
/// A new row exists only when the normalized content changed, so <see cref="ContentHash"/> differs
/// from the previous version's.
/// </summary>
public class ConfigVersion
{
    public long Id { get; set; }
    public int NodeId { get; set; }
    public Node Node { get; set; } = null!;
    public DateTime CapturedUtc { get; set; }

    /// <summary>SHA-256 (hex) of the normalized text, as computed when the version was captured.</summary>
    public required string ContentHash { get; set; }

    /// <summary>Encrypted raw config, UTF-8. Decrypt only through <c>ConfigBackupService</c>.</summary>
    public required byte[] ProtectedRaw { get; set; }

    public int RawLength { get; set; }
    public int LineCount { get; set; }
}
