using Yanmt.Core.Credentials;

namespace Yanmt.Infrastructure.Data.Entities;

/// <summary>
/// A reusable SSH or SNMPv3 credential. Non-secret fields are plain columns; passwords live only in
/// <see cref="ProtectedSecrets"/>, a DPAPI blob of a small JSON document (see CredentialService).
/// </summary>
public class CredentialProfile : IAudited
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public CredentialKind Kind { get; set; }
    public required string Username { get; set; }
    public int Port { get; set; }

    /// <summary>SNMPv3 only.</summary>
    public SnmpAuthProtocol? SnmpAuth { get; set; }

    /// <summary>SNMPv3 only.</summary>
    public SnmpPrivProtocol? SnmpPriv { get; set; }

    [Secret]
    public required byte[] ProtectedSecrets { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
