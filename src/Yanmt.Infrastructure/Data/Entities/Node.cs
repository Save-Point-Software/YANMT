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
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public List<NodeTag> Tags { get; set; } = [];
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
