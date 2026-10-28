using Yanmt.Core.ConfigBackups;

namespace Yanmt.Core.Tests.ConfigBackups;

public class ConfigSecretMaskerTests
{
    [Theory]
    [InlineData("enable secret 9 $9$FAKE$hash", "enable secret 9 <masked>")]
    [InlineData("username a privilege 15 secret 9 $9$FAKE$hash", "username a privilege 15 secret 9 <masked>")]
    [InlineData("username b password 7 0000FAKE0000", "username b password 7 <masked>")]
    [InlineData(" password 0 Fake Pass With Spaces", " password 0 <masked>")]
    [InlineData("enable password level 5 7 0000FAKE", "enable password level 5 7 <masked>")]
    [InlineData("  key-string 7 0000FAKEKEY00", "  key-string 7 <masked>")]
    [InlineData("tacacs-server host 203.0.113.5 key 7 0000FAKETAC00", "tacacs-server host 203.0.113.5 key 7 <masked>")]
    [InlineData("crypto isakmp key FakePsk address 192.0.2.9", "crypto isakmp key <masked>")]
    [InlineData(" pre-shared-key local 0 FakePsk", " pre-shared-key local 0 <masked>")]
    [InlineData("ntp authentication-key 1 md5 FakeNtpKey", "ntp authentication-key 1 <masked>")]
    [InlineData("snmp-server community FakeCommunity RO", "snmp-server community <masked>")]
    [InlineData("snmp-server user u g v3 auth sha FakeAuth1 priv aes 128 FakePriv2", "snmp-server user u g v3 auth sha <masked> priv aes 128 <masked>")]
    [InlineData("snmp-server user u g v3 auth sha256 FakeAuth1", "snmp-server user u g v3 auth sha256 <masked>")]
    [InlineData(" ip nhrp authentication FakeNhrp", " ip nhrp authentication <masked>")]
    [InlineData(" ip ospf message-digest-key 1 md5 FakeOspf", " ip ospf message-digest-key 1 md5 <masked>")]
    [InlineData(" ip ospf message-digest-key 1 md5 7 0000FAKEOSPF", " ip ospf message-digest-key 1 md5 7 <masked>")]
    [InlineData("snmp-server host 198.51.100.7 version 2c FakeTrapComm", "snmp-server host 198.51.100.7 version 2c <masked>")]
    [InlineData("snmp-server host 198.51.100.7 traps version 2c FakeTrapComm config", "snmp-server host 198.51.100.7 traps version 2c <masked>")]
    [InlineData(" standby 1 authentication FakeHsrp", " standby 1 authentication <masked>")]
    [InlineData(" standby 1 authentication text FakeHsrp", " standby 1 authentication text <masked>")]
    [InlineData(" standby 1 authentication md5 key-string 7 0000FAKE", " standby 1 authentication md5 key-string 7 <masked>")]
    [InlineData(" vrrp 1 authentication text FakeVrrp", " vrrp 1 authentication text <masked>")]
    public void Masks_secret_values(string line, string expected)
    {
        Assert.Equal(expected, ConfigSecretMasker.MaskLine(line));
        Assert.True(ConfigSecretMasker.IsSecretLine(line));
    }

    [Theory]
    [InlineData("hostname lab-rtr-01")]
    [InlineData("key chain LAB-KEYS")]
    [InlineData(" key 1")]
    [InlineData("crypto key generate rsa modulus 2048")]
    [InlineData("ip ssh pubkey-chain")]
    [InlineData("ip ssh version 2")]
    [InlineData("ip address 192.0.2.1 255.255.255.0")]
    [InlineData("snmp-server host 198.51.100.7 version 3 priv labmon")]
    [InlineData("no service password-encryption")]
    [InlineData("service password-encryption")]
    [InlineData("login local")]
    [InlineData(" tunnel key 100")]
    [InlineData(" standby 1 authentication md5 key-chain LAB-KEYS")]
    [InlineData(" ip ospf authentication message-digest")]
    [InlineData("!")]
    public void Leaves_other_lines_alone(string line)
    {
        Assert.Equal(line, ConfigSecretMasker.MaskLine(line));
        Assert.False(ConfigSecretMasker.IsSecretLine(line));
    }

    [Fact]
    public void Masks_the_fixture_without_changing_line_count_and_leaks_no_secret()
    {
        var masked = ConfigSecretMasker.MaskSecrets(ConfigFixtures.Router);

        Assert.Equal(ConfigFixtures.Router.Split('\n').Length, masked.Split('\n').Length);
        foreach (var secret in new[] { "notarealhash", "anotherfakehash", "0000FAKE0000", "0000FAKEKEY00", "FakeCommunity",
                     "FakeAuthPass123", "FakePrivPass456", "0000FAKETAC00", "FakeVtyPassword" })
        {
            Assert.DoesNotContain(secret, masked);
        }

        Assert.Contains("hostname lab-rtr-01", masked);
        Assert.Contains("key chain LAB-KEYS", masked);
        Assert.Contains("crypto key generate rsa modulus 2048", masked);
    }

    [Fact]
    public void Preserves_crlf_endings()
    {
        Assert.Equal("a\r\nenable secret 9 <masked>\r\nb", ConfigSecretMasker.MaskSecrets("a\r\nenable secret 9 xyz\r\nb"));
    }

    [Fact]
    public void Empty_and_trailing_newline_round_trip()
    {
        Assert.Equal("", ConfigSecretMasker.MaskSecrets(""));
        Assert.Equal("hostname x\n", ConfigSecretMasker.MaskSecrets("hostname x\n"));
    }
}
