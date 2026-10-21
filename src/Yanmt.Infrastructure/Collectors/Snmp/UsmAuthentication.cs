using System.Security.Cryptography;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Yanmt.Core.Credentials;
using Yanmt.Infrastructure.Credentials;

namespace Yanmt.Infrastructure.Collectors.Snmp;

/// <summary>
/// Verifies the USM HMAC of a received SNMPv3 message (RFC 3414 §6.3.2 and HMAC-SHA-96 for SHA-1,
/// RFC 7860 for SHA-2).
/// SharpSnmpLib 12.5.7 decrypts responses but does not check their HMAC, so without this a
/// spoofed or altered response would be accepted. The check runs on the raw datagram: the
/// msgAuthenticationParameters field is zeroed, the HMAC is computed with the localized auth key,
/// and the truncated result is compared in constant time. Anything unexpected fails closed.
/// </summary>
internal static class UsmAuthentication
{
    public static bool Verify(byte[] datagram, ISnmpMessage message, SnmpV3Credential credential)
    {
        var engineId = message.Parameters?.EngineId?.GetRaw();
        if (engineId is null || engineId.Length == 0)
        {
            return false;
        }

        // SharpSnmpLib caches localized keys process-wide and hands back the cached array, which it
        // also signs outgoing requests with. Never modify (e.g. zero) it: doing so made every later
        // request for the same password and engine go out signed with a zero key (usmStatsWrongDigests).
        var privacy = SnmpV3Reader.CreatePrivacy(credential);
        var key = privacy.AuthenticationProvider.PasswordToKey(new OctetString(credential.AuthPassword).GetRaw(), engineId);
        return Verify(datagram, message, credential.Auth, key);
    }

    /// <summary>Verifies with an already-localized key. Separate so tests can supply an independently derived key.</summary>
    internal static bool Verify(byte[] datagram, ISnmpMessage message, SnmpAuthProtocol auth, ReadOnlySpan<byte> localizedKey)
    {
        var received = message.Parameters?.AuthenticationParameters?.GetRaw();
        if (received is null || received.Length != DigestLength(auth) || localizedKey.IsEmpty)
        {
            return false;
        }

        // Find the authParams TLV (OCTET STRING, short-form length) carrying exactly the received digest.
        // A random digest can't plausibly appear twice; if it does, refuse rather than guess.
        ReadOnlySpan<byte> tlv = [0x04, (byte)received.Length, .. received];
        var offset = datagram.AsSpan().IndexOf(tlv);
        if (offset < 0 || datagram.AsSpan(offset + 1).IndexOf(tlv) >= 0)
        {
            return false;
        }

        var zeroed = (byte[])datagram.Clone();
        Array.Clear(zeroed, offset + 2, received.Length);

        var mac = auth switch
        {
            SnmpAuthProtocol.Sha1 => HMACSHA1.HashData(localizedKey, zeroed),
            SnmpAuthProtocol.Sha256 => HMACSHA256.HashData(localizedKey, zeroed),
            SnmpAuthProtocol.Sha384 => HMACSHA384.HashData(localizedKey, zeroed),
            SnmpAuthProtocol.Sha512 => HMACSHA512.HashData(localizedKey, zeroed),
            _ => [],
        };
        return mac.Length >= received.Length && CryptographicOperations.FixedTimeEquals(mac.AsSpan(0, received.Length), received);
    }

    /// <summary>usmHMACSHAAuthProtocol (HMAC-SHA-96, RFC 3414 §7), usmHMAC192SHA256, usmHMAC256SHA384, usmHMAC384SHA512 (RFC 7860 §4.2.1).</summary>
    public static int DigestLength(SnmpAuthProtocol auth) => auth switch
    {
        SnmpAuthProtocol.Sha1 => 12,
        SnmpAuthProtocol.Sha256 => 24,
        SnmpAuthProtocol.Sha384 => 32,
        SnmpAuthProtocol.Sha512 => 48,
        _ => throw new ArgumentOutOfRangeException(nameof(auth)),
    };

    /// <summary>True when the message header says it was both authenticated and encrypted.</summary>
    public static bool IsAuthPriv(ISnmpMessage message)
    {
        var level = message.Header.SecurityLevel;
        return level.HasFlag(Levels.Authentication) && level.HasFlag(Levels.Privacy);
    }
}
