using System;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Yanmt.Installer.CustomActions;

/// <summary>The YANMT service and its virtual account, NT SERVICE\YANMT.</summary>
internal static class ServiceIdentity
{
    public const string ServiceName = "YANMT";
    public const string Account = @"NT SERVICE\" + ServiceName;

    /// <summary>
    /// The service SID: S-1-5-80 followed by the SHA-1 of the upper-case service name (UTF-16LE) as five
    /// little-endian 32-bit values. Fixed for the name, so it is known before the service exists and
    /// without a name lookup. For YANMT: S-1-5-80-494844833-2242255151-845590027-1396523193-2268386581
    /// (the CI smoke test compares it with "sc.exe showsid YANMT").
    /// </summary>
    public static SecurityIdentifier Sid { get; } = Compute(ServiceName);

    internal static SecurityIdentifier Compute(string serviceName)
    {
        byte[] hash;
        using (var sha1 = SHA1.Create())
        {
            hash = sha1.ComputeHash(Encoding.Unicode.GetBytes(serviceName.ToUpperInvariant()));
        }

        var sb = new StringBuilder("S-1-5-80");
        for (var i = 0; i < 5; i++)
        {
            sb.Append('-').Append(BitConverter.ToUInt32(hash, i * 4));
        }

        return new SecurityIdentifier(sb.ToString());
    }
}
