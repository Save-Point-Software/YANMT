using System.Text.RegularExpressions;
using Yanmt.Core.ConfigBackups;

namespace Yanmt.Core.Tests.ConfigBackups;

public class ConfigNormalizerTests
{
    [Fact]
    public void Strips_volatile_lines_and_keeps_the_rest()
    {
        var n = ConfigNormalizer.Normalize(ConfigFixtures.Router);

        Assert.DoesNotContain("Building configuration", n);
        Assert.DoesNotContain("Current configuration", n);
        Assert.DoesNotContain("Last configuration change", n);
        Assert.DoesNotContain("NVRAM config last updated", n);
        Assert.DoesNotContain("ntp clock-period", n);
        Assert.Contains("hostname lab-rtr-01", n);
        Assert.Contains("ntp server 198.51.100.10", n);
        Assert.StartsWith("!\n!\nversion 17.9\n", n);
        Assert.EndsWith("end\n", n);
    }

    [Fact]
    public void Volatile_values_do_not_change_the_hash()
    {
        var later = ConfigFixtures.Router
            .Replace("4821 bytes", "4833 bytes")
            .Replace("10:15:42 UTC Mon Jan 5 2026", "08:00:00 UTC Tue Jan 6 2026")
            .Replace("ntp clock-period 17179869", "ntp clock-period 17179902");

        Assert.Equal(
            ConfigBackupEvaluator.Hash(ConfigNormalizer.Normalize(ConfigFixtures.Router)),
            ConfigBackupEvaluator.Hash(ConfigNormalizer.Normalize(later)));
    }

    [Fact]
    public void Real_change_changes_the_hash()
    {
        var changed = ConfigFixtures.Router.Replace("192.0.2.1 255.255.255.0", "192.0.2.2 255.255.255.0");

        Assert.NotEqual(
            ConfigBackupEvaluator.Hash(ConfigNormalizer.Normalize(ConfigFixtures.Router)),
            ConfigBackupEvaluator.Hash(ConfigNormalizer.Normalize(changed)));
    }

    [Fact]
    public void Line_endings_and_trailing_whitespace_are_ignored()
    {
        var crlf = ConfigFixtures.Router.Replace("\n", "  \r\n");
        Assert.Equal(ConfigNormalizer.Normalize(ConfigFixtures.Router), ConfigNormalizer.Normalize(crlf));
    }

    [Fact]
    public void Extra_patterns_drop_matching_lines()
    {
        var n = ConfigNormalizer.Normalize(ConfigFixtures.Router, ["^ntp server", "SNMP-SERVER COMMUNITY"]);
        Assert.DoesNotContain("ntp server", n);
        Assert.DoesNotContain("snmp-server community", n);
        Assert.Contains("hostname lab-rtr-01", n);
    }

    [Fact]
    public void Blank_patterns_are_ignored_and_empty_input_gives_empty_output()
    {
        Assert.Equal("a\n", ConfigNormalizer.Normalize("a", ["", "  "]));
        Assert.Equal(string.Empty, ConfigNormalizer.Normalize("Building configuration...\n\n"));
    }

    [Fact]
    public void Invalid_pattern_throws_instead_of_being_skipped()
    {
        Assert.ThrowsAny<ArgumentException>(() => ConfigNormalizer.Normalize("a", ["("]));
    }

    [Fact]
    public void Runaway_pattern_times_out_instead_of_hanging()
    {
        var input = new string('a', 40) + "!";
        Assert.Throws<RegexMatchTimeoutException>(() => ConfigNormalizer.Normalize(input, ["^(a+)+$"]));
    }
}
