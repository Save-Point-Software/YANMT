using Yanmt.Core.Inventory;

namespace Yanmt.Infrastructure.Data.Entities;

public class Node : IAudited
{
    public int Id { get; set; }
    public required string Hostname { get; set; }

    /// <summary>Canonical text form of the management IPv4/IPv6 address.</summary>
    public required string ManagementIp { get; set; }

    public string? Site { get; set; }
    public NodeIconType Icon { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IcmpEnabled { get; set; } = true;
    public bool SnmpEnabled { get; set; }
    public bool SshEnabled { get; set; }

    public int? SnmpProfileId { get; set; }
    public CredentialProfile? SnmpProfile { get; set; }
    public int? SshProfileId { get; set; }
    public CredentialProfile? SshProfile { get; set; }

    /// <summary>Host key algorithm pinned on first successful SSH connection (trust on first use).</summary>
    public string? SshHostKeyAlgorithm { get; set; }

    /// <summary>"SHA256:…" fingerprint of the pinned host key. Connections presenting another key are refused.</summary>
    public string? SshHostKeyFingerprint { get; set; }

    /// <summary>Per-node CPU/memory thresholds (percent). Null uses the global SNMP setting.</summary>
    public double? CpuWarningPercent { get; set; }

    public double? CpuCriticalPercent { get; set; }
    public double? MemoryWarningPercent { get; set; }
    public double? MemoryCriticalPercent { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public List<NodeTag> Tags { get; set; } = [];
}

/// <summary>
/// What the last SNMP poll learned about a node: system group, uptime baseline, engine. Written by
/// the status engine on every poll, so it is deliberately not audited and lives apart from Node.
/// </summary>
public class NodeSystemInfo
{
    public int NodeId { get; set; }
    public Node Node { get; set; } = null!;
    public string? SysDescr { get; set; }
    public string? SysObjectId { get; set; }
    public string? SysName { get; set; }
    public string? SysLocation { get; set; }
    public string? SysContact { get; set; }

    /// <summary>sysUpTime (TimeTicks) at <see cref="LastSuccessUtc"/>, the baseline for reboot detection.</summary>
    public long? UptimeTicks { get; set; }

    public string? EngineId { get; set; }
    public int? EngineBoots { get; set; }

    /// <summary>The memory pool the Memory source reports (normally "Processor").</summary>
    public string? MemoryPool { get; set; }

    public DateTime? LastPollUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public DateTime? LastRebootUtc { get; set; }
}

public class Tag
{
    public int Id { get; set; }
    public required string Name { get; set; }

    public List<NodeTag> Nodes { get; set; } = [];
}

public class NodeTag : IAudited
{
    public int NodeId { get; set; }
    public Node Node { get; set; } = null!;
    public int TagId { get; set; }
    public Tag Tag { get; set; } = null!;
}
