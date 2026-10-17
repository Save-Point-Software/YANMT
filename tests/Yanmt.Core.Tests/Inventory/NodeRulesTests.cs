using Yanmt.Core.Inventory;

namespace Yanmt.Core.Tests.Inventory;

public class NodeRulesTests
{
    [Theory]
    [InlineData("rtr-hub-01")]
    [InlineData("SW1.lab.example")]
    [InlineData("fw_edge")]
    [InlineData("9k-core")]
    public void Accepts_valid_hostnames(string hostname)
    {
        Assert.Null(NodeRules.ValidateHostname(hostname));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-leading-dash")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("rtr<script>")]
    public void Rejects_invalid_hostnames(string hostname)
    {
        Assert.NotNull(NodeRules.ValidateHostname(hostname));
    }

    [Fact]
    public void Rejects_overlong_hostname()
    {
        Assert.NotNull(NodeRules.ValidateHostname(new string('a', NodeRules.MaxHostnameLength + 1)));
    }

    [Theory]
    [InlineData("192.0.2.1", "192.0.2.1")]
    [InlineData(" 198.51.100.254 ", "198.51.100.254")]
    [InlineData("2001:DB8::1", "2001:db8::1")]
    [InlineData("2001:0db8:0000:0000:0000:0000:0000:0010", "2001:db8::10")]
    public void Normalizes_valid_addresses(string input, string expected)
    {
        Assert.True(NodeRules.TryNormalizeIp(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("203.0.113")]
    [InlineData("10")]
    [InlineData("192.0.2.256")]
    [InlineData("192.0.2.1/24")]
    [InlineData("rtr-hub-01")]
    [InlineData("fe80::1%3")]
    [InlineData("")]
    public void Rejects_invalid_addresses(string input)
    {
        Assert.NotNull(NodeRules.ValidateManagementIp(input));
    }

    [Fact]
    public void ParseTags_splits_trims_and_dedupes_case_insensitively()
    {
        Assert.Equal(["core", "lab", "dmvpn hub"], NodeRules.ParseTags(" core; lab ,Core;; dmvpn hub "));
        Assert.Empty(NodeRules.ParseTags(null));
        Assert.Empty(NodeRules.ParseTags(" ; "));
    }

    [Theory]
    [InlineData("core", true)]
    [InlineData("dmvpn hub", true)]
    [InlineData("site-1.a_b", true)]
    [InlineData("bad=tag", false)]
    [InlineData("<b>", false)]
    public void ValidateTag(string tag, bool valid)
    {
        Assert.Equal(valid, NodeRules.ValidateTag(tag) is null);
    }
}
