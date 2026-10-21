using System.Security.Cryptography;
using System.Text;
using Yanmt.Core.Credentials;

namespace Yanmt.Infrastructure.Tests.Collectors;

/// <summary>
/// An independent implementation of USM password-to-key and key localization (RFC 3414 §A.2,
/// RFC 7860 §9.3 for SHA-2), so the fake agent and the HMAC tests never share SharpSnmpLib's
/// process-wide key cache with the code under test.
/// </summary>
internal static class Rfc3414Keys
{
    public static byte[] Localize(SnmpAuthProtocol auth, string password, byte[] engineId)
    {
        var name = auth switch
        {
            SnmpAuthProtocol.Sha1 => HashAlgorithmName.SHA1,
            SnmpAuthProtocol.Sha256 => HashAlgorithmName.SHA256,
            SnmpAuthProtocol.Sha384 => HashAlgorithmName.SHA384,
            SnmpAuthProtocol.Sha512 => HashAlgorithmName.SHA512,
            _ => throw new ArgumentOutOfRangeException(nameof(auth)),
        };
        var pw = Encoding.UTF8.GetBytes(password);
        using var hash = IncrementalHash.CreateHash(name);

        // Ku: hash of the password repeated to fill 1,048,576 octets.
        var block = new byte[64];
        for (int done = 0, index = 0; done < 1_048_576; done += block.Length)
        {
            for (var i = 0; i < block.Length; i++)
            {
                block[i] = pw[index++ % pw.Length];
            }

            hash.AppendData(block);
        }

        var ku = hash.GetHashAndReset();

        // Kul = H(Ku || engineID || Ku).
        hash.AppendData(ku);
        hash.AppendData(engineId);
        hash.AppendData(ku);
        return hash.GetHashAndReset();
    }
}
