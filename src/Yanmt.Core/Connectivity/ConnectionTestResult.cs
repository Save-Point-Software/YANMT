namespace Yanmt.Core.Connectivity;

public enum ConnectionTestKind
{
    Icmp,
    Snmp,
    Ssh,
}

/// <summary>Why a connection test failed, so the UI can say what to fix rather than show a stack trace.</summary>
public enum FailureCategory
{
    /// <summary>The node has no profile assigned, or the protocol is disabled for it.</summary>
    NotConfigured,

    /// <summary>The stored secret can't be decrypted (DB restored from another server, or entropy file lost).</summary>
    CredentialUnreadable,

    Timeout,
    Unreachable,
    Refused,
    AuthFailure,
    UnknownUser,
    UnknownEngine,
    NotInTimeWindow,
    DecryptionError,
    UnsupportedSecurityLevel,
    UnsupportedAlgorithm,

    /// <summary>The SSH host key differs from the one pinned for this node.</summary>
    HostKeyChanged,

    /// <summary>The device answered, but with an error (e.g. noSuchObject, a non-zero exit status).</summary>
    DeviceError,

    Other,
}

/// <param name="Output">Device output worth showing (sysDescr, the start of show version).</param>
/// <param name="PresentedHostKey">
/// For a <see cref="FailureCategory.HostKeyChanged"/> failure, the "SHA256:…" fingerprint the device
/// offered, so the UI can offer to trust exactly that key.
/// </param>
public sealed record ConnectionTestResult(
    ConnectionTestKind Kind,
    bool Success,
    FailureCategory? Failure,
    string Summary,
    IReadOnlyList<KeyValuePair<string, string>> Details,
    TimeSpan Elapsed,
    string? Output = null,
    string? PresentedHostKey = null)
{
    public static ConnectionTestResult Ok(ConnectionTestKind kind, string summary, IReadOnlyList<KeyValuePair<string, string>> details, TimeSpan elapsed, string? output = null) =>
        new(kind, true, null, summary, details, elapsed, output);

    public static ConnectionTestResult Fail(ConnectionTestKind kind, FailureCategory failure, string summary, TimeSpan elapsed, IReadOnlyList<KeyValuePair<string, string>>? details = null) =>
        new(kind, false, failure, summary, details ?? [], elapsed);
}

/// <summary>
/// SNMPv3 USM report counters (RFC 3414 §5). An agent answers a bad request with a Report PDU
/// carrying one of these, which tells us exactly which part of the credential is wrong.
/// </summary>
public static class UsmReport
{
    public const string UnsupportedSecLevels = "1.3.6.1.6.3.15.1.1.1.0";
    public const string NotInTimeWindows = "1.3.6.1.6.3.15.1.1.2.0";
    public const string UnknownUserNames = "1.3.6.1.6.3.15.1.1.3.0";
    public const string UnknownEngineIds = "1.3.6.1.6.3.15.1.1.4.0";
    public const string WrongDigests = "1.3.6.1.6.3.15.1.1.5.0";
    public const string DecryptionErrors = "1.3.6.1.6.3.15.1.1.6.0";

    public static (FailureCategory Category, string Message) Classify(string reportOid) => reportOid.TrimStart('.') switch
    {
        UnknownUserNames => (FailureCategory.UnknownUser,
            "The device doesn't know this SNMPv3 user (usmStatsUnknownUserNames). Check the username and that the user exists on the device."),
        WrongDigests => (FailureCategory.AuthFailure,
            "Authentication failed (usmStatsWrongDigests): wrong auth password or auth protocol."),
        DecryptionErrors => (FailureCategory.DecryptionError,
            "The device couldn't decrypt the request (usmStatsDecryptionErrors): wrong privacy password or privacy protocol."),
        UnsupportedSecLevels => (FailureCategory.UnsupportedSecurityLevel,
            "The device rejected authPriv for this user (usmStatsUnsupportedSecLevels). Check that the user is configured with auth and priv."),
        NotInTimeWindows => (FailureCategory.NotInTimeWindow,
            "The request was outside the device's time window (usmStatsNotInTimeWindows). Retrying with fresh engine time usually fixes this."),
        UnknownEngineIds => (FailureCategory.UnknownEngine,
            "The device didn't recognize the engine ID (usmStatsUnknownEngineIDs). Engine discovery may have failed."),
        _ => (FailureCategory.DeviceError, $"The device sent an unexpected SNMP report ({reportOid})."),
    };
}
