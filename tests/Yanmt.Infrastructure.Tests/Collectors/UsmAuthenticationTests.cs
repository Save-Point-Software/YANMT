using System.Security.Cryptography;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using Yanmt.Core.Credentials;
using Yanmt.Infrastructure.Collectors.Snmp;
using Yanmt.Infrastructure.Credentials;

namespace Yanmt.Infrastructure.Tests.Collectors;

/// <summary>
/// Known-answer checks for the response HMAC verification, anchored on RFC 3414 rather than on
/// SharpSnmpLib, so a pass doesn't just mean "agrees with the library that signed it".
/// </summary>
public class UsmAuthenticationTests
{
    // RFC 3414 Appendix A.3.2: password "maplesyrup", engine ID 00..02, SHA-1 localized key.
    private const string RfcPassword = "maplesyrup";
    private static readonly byte[] RfcEngineId = Convert.FromHexString("000000000000000000000002");
    private static readonly byte[] RfcSha1LocalizedKey = Convert.FromHexString("6695febc9288e36282235fc7151f128497b38f3f");

    private static SnmpV3Credential Sha1Cred(string authPassword = RfcPassword) =>
        new("yanmt-sha1", SnmpAuthProtocol.Sha1, authPassword, SnmpPrivProtocol.Aes128, "priv-pass-0001");

    /// <summary>An authPriv response signed by SharpSnmpLib with the given credential, from the RFC engine.</summary>
    private static (byte[] Datagram, ISnmpMessage Message) SignedResponse(SnmpV3Credential credential)
    {
        var privacy = SnmpV3Reader.CreatePrivacy(credential);
        var engineId = new OctetString(RfcEngineId);
        var message = new ResponseMessage(
            VersionCode.V3,
            new Header(new Integer32(4242), new Integer32(Messenger.MaxMessageSize), privacy.ToSecurityLevel()),
            new SecurityParameters(engineId, new Integer32(3), new Integer32(1000), new OctetString(credential.Username),
                privacy.AuthenticationProvider.CleanDigest, privacy.Salt),
            new Scope(engineId, OctetString.Empty, new ResponsePdu(7, ErrorCode.NoError, 0,
                [new Variable(new ObjectIdentifier("1.3.6.1.2.1.1.5.0"), new OctetString("lab-rtr-isp-01"))])),
            privacy,
            true,
            null);
        var datagram = message.ToBytes();
        var registry = new UserRegistry();
        registry.Add(new OctetString(credential.Username), privacy);
        return (datagram, MessageFactory.ParseMessages(datagram, registry)[0]);
    }

    [Fact]
    public void Sha1_key_localization_matches_rfc3414_test_vector()
    {
        var privacy = SnmpV3Reader.CreatePrivacy(Sha1Cred());
        var key = privacy.AuthenticationProvider.PasswordToKey(new OctetString(RfcPassword).GetRaw(), RfcEngineId);

        Assert.Equal(RfcSha1LocalizedKey, key);
    }

    [Fact]
    public void Independent_key_derivation_matches_rfc3414_test_vector() =>
        Assert.Equal(RfcSha1LocalizedKey, Rfc3414Keys.Localize(SnmpAuthProtocol.Sha1, RfcPassword, RfcEngineId));

    [Theory]
    [InlineData(SnmpAuthProtocol.Sha1)]
    [InlineData(SnmpAuthProtocol.Sha256)]
    [InlineData(SnmpAuthProtocol.Sha384)]
    [InlineData(SnmpAuthProtocol.Sha512)]
    public void Library_key_localization_matches_the_independent_one(SnmpAuthProtocol auth)
    {
        var cred = new SnmpV3Credential("yanmt-ro", auth, RfcPassword, SnmpPrivProtocol.Aes128, "priv-pass-0001");
        var key = SnmpV3Reader.CreatePrivacy(cred).AuthenticationProvider.PasswordToKey(new OctetString(RfcPassword).GetRaw(), RfcEngineId);

        Assert.Equal(Rfc3414Keys.Localize(auth, RfcPassword, RfcEngineId), key);
    }

    [Fact]
    public void Verify_leaves_the_library_key_cache_intact()
    {
        // Regression: Verify used to zero the key array, which SharpSnmpLib caches and reuses to sign
        // later requests, so the next request with the same password and engine was refused.
        var (datagram, message) = SignedResponse(Sha1Cred());
        Assert.True(UsmAuthentication.Verify(datagram, message, Sha1Cred()));
        Assert.True(UsmAuthentication.Verify(datagram, message, Sha1Cred()));

        var key = SnmpV3Reader.CreatePrivacy(Sha1Cred()).AuthenticationProvider.PasswordToKey(new OctetString(RfcPassword).GetRaw(), RfcEngineId);
        Assert.Equal(RfcSha1LocalizedKey, key);
    }

    [Fact]
    public void Sha1_response_digest_is_hmac_sha_96_with_the_rfc_key_and_verifies()
    {
        var (datagram, message) = SignedResponse(Sha1Cred());
        var digest = message.Parameters!.AuthenticationParameters!.GetRaw();
        Assert.Equal(12, digest.Length);

        // Recompute independently: zero the digest in place, HMAC-SHA-1 with the RFC key, keep 96 bits.
        ReadOnlySpan<byte> tlv = [0x04, 12, .. digest];
        var offset = datagram.AsSpan().IndexOf(tlv);
        Assert.True(offset >= 0);
        var zeroed = (byte[])datagram.Clone();
        Array.Clear(zeroed, offset + 2, 12);
        Assert.Equal(digest, HMACSHA1.HashData(RfcSha1LocalizedKey, zeroed)[..12]);

        Assert.True(UsmAuthentication.Verify(datagram, message, Sha1Cred()));
    }

    [Fact]
    public void Sha1_response_signed_with_another_password_is_rejected()
    {
        var (datagram, message) = SignedResponse(Sha1Cred("forger-auth-0001"));

        Assert.False(UsmAuthentication.Verify(datagram, message, Sha1Cred()));
    }

    [Fact]
    public void Sha1_response_with_any_altered_byte_is_rejected()
    {
        var (datagram, message) = SignedResponse(Sha1Cred());
        var tampered = (byte[])datagram.Clone();
        tampered[^1] ^= 0x01; // inside the encrypted scoped PDU

        Assert.False(UsmAuthentication.Verify(tampered, message, Sha1Cred()));
    }

    [Fact]
    public void Digest_length_must_match_the_configured_protocol()
    {
        // A 12-byte SHA-1 digest is never accepted for a SHA-256 profile (which expects 24 bytes).
        var (datagram, message) = SignedResponse(Sha1Cred());
        var sha256 = new SnmpV3Credential("yanmt-sha1", SnmpAuthProtocol.Sha256, RfcPassword, SnmpPrivProtocol.Aes128, "priv-pass-0001");

        Assert.False(UsmAuthentication.Verify(datagram, message, sha256));
    }

    [Theory]
    [InlineData(SnmpAuthProtocol.Sha1, 12)]
    [InlineData(SnmpAuthProtocol.Sha256, 24)]
    [InlineData(SnmpAuthProtocol.Sha384, 32)]
    [InlineData(SnmpAuthProtocol.Sha512, 48)]
    public void Digest_lengths_follow_rfc3414_and_rfc7860(SnmpAuthProtocol auth, int length) =>
        Assert.Equal(length, UsmAuthentication.DigestLength(auth));
}
