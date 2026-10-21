using Yanmt.Core.Status;

namespace Yanmt.Infrastructure.Data.Entities;

public enum StatusSourceType
{
    Icmp,
    Cpu,
    Memory,
    Uptime,
    Custom,
    Backup,
}

/// <summary>
/// Current state of one status source on one node. This is the single table the node and
/// submap rollups read. <see cref="SourceKey"/> distinguishes multiple sources of the same
/// type on a node (for custom monitors: the monitor instance id); it is "" when there is only one.
/// </summary>
public class StatusSource
{
    public long Id { get; set; }
    public int NodeId { get; set; }
    public Node Node { get; set; } = null!;
    public StatusSourceType SourceType { get; set; }
    public string SourceKey { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public Severity State { get; set; } = Severity.Unknown;
    public double? Value { get; set; }
    public string? ValueText { get; set; }
    public DateTime SinceUtc { get; set; }
    public DateTime? LastUpdateUtc { get; set; }

    /// <summary>Candidate state waiting on consecutive-match dampening.</summary>
    public Severity? PendingState { get; set; }

    public int PendingCount { get; set; }
}

/// <summary>One ICMP poll of one node. Latencies are null when no echo was answered.</summary>
public class IcmpSample
{
    public long Id { get; set; }
    public int NodeId { get; set; }
    public Node Node { get; set; } = null!;
    public DateTime TimestampUtc { get; set; }
    public int Sent { get; set; }
    public int Received { get; set; }
    public double? MinMs { get; set; }
    public double? AvgMs { get; set; }
    public double? MaxMs { get; set; }
    public double LossPercent { get; set; }
}

/// <summary>One numeric sample of a status source (CPU %, memory %, later custom monitors).</summary>
public class MetricSample
{
    public long Id { get; set; }
    public long StatusSourceId { get; set; }
    public StatusSource StatusSource { get; set; } = null!;
    public DateTime TimestampUtc { get; set; }
    public double Value { get; set; }
}

/// <summary>A state change from any source. Acknowledgement and the event UI arrive in Phase 6.</summary>
public class Event
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public int? NodeId { get; set; }
    public Node? Node { get; set; }
    public StatusSourceType Source { get; set; }
    public string SourceKey { get; set; } = "";
    public Severity? OldState { get; set; }
    public Severity NewState { get; set; }
    public required string Message { get; set; }
    public string? AcknowledgedBy { get; set; }
    public DateTime? AcknowledgedUtc { get; set; }
}

/// <summary>Operational setting editable in the UI. Bootstrap settings live in appsettings.json instead.</summary>
public class Setting : IAudited
{
    public required string Key { get; set; }
    public required string JsonValue { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
