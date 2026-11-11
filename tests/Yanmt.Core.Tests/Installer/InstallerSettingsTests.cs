using Yanmt.Core.Installer;

namespace Yanmt.Core.Tests.Installer;

public class InstallerSettingsTests
{
    private static IReadOnlyDictionary<string, string?> Map(Dictionary<string, object?> values, int admins = 0, int viewers = 0) =>
        InstallerSettings.ToConfiguration(values, admins, viewers);

    [Fact]
    public void No_values_set_nothing() => Assert.Empty(Map([]));

    [Fact]
    public void All_values_map_to_their_configuration_keys()
    {
        var config = Map(new()
        {
            ["Port"] = 9443,
            ["CertificateThumbprint"] = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678",
            ["AdminGroups"] = @"CONTOSO\YANMT-Admins;.\YANMT-Admins",
            ["ViewerGroups"] = @"CONTOSO\YANMT-Viewers",
        });

        Assert.Equal("9443", config["Yanmt:Https:Port"]);
        Assert.Equal("A1B2C3D4E5F60718293A4B5C6D7E8F9012345678", config["Yanmt:Https:CertificateThumbprint"]);
        Assert.Equal(@"CONTOSO\YANMT-Admins", config["Yanmt:Roles:AdminGroups:0"]);
        Assert.Equal(@".\YANMT-Admins", config["Yanmt:Roles:AdminGroups:1"]);
        Assert.Equal(@"CONTOSO\YANMT-Viewers", config["Yanmt:Roles:ViewerGroups:0"]);
        Assert.Equal(5, config.Count);
    }

    [Fact]
    public void Value_names_are_case_insensitive_like_the_registry() =>
        Assert.Equal("8443", Map(new() { ["port"] = "8443" })["Yanmt:Https:Port"]);

    [Fact]
    public void Port_as_string_is_accepted_and_trimmed() =>
        Assert.Equal("8443", Map(new() { ["Port"] = " 8443 " })["Yanmt:Https:Port"]);

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("84 43")]
    [InlineData("https")]
    public void Invalid_port_fails_with_the_registry_path(string port)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Map(new() { ["Port"] = port }));
        Assert.Contains(@"HKLM\SOFTWARE\YANMT\Port", ex.Message);
    }

    [Fact]
    public void Invalid_port_as_dword_fails() =>
        Assert.Throws<InvalidOperationException>(() => Map(new() { ["Port"] = 70000 }));

    [Fact]
    public void Thumbprint_copied_from_the_certificate_dialog_is_cleaned()
    {
        var config = Map(new() { ["CertificateThumbprint"] = "‎a1 b2 c3 d4 e5 f6 07 18 29 3a 4b 5c 6d 7e 8f 90 12 34 56 78" });
        Assert.Equal("A1B2C3D4E5F60718293A4B5C6D7E8F9012345678", config["Yanmt:Https:CertificateThumbprint"]);
    }

    [Theory]
    [InlineData("not-a-thumbprint")]
    [InlineData("A1B2C3D4")]
    [InlineData("A1B2C3D4E5F60718293A4B5C6D7E8F901234567800")]
    [InlineData("A1B2C3D4E5F60718293A4B5C6D7E8F901234567G")]
    public void Malformed_thumbprint_fails(string thumbprint)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Map(new() { ["CertificateThumbprint"] = thumbprint }));
        Assert.Contains("40 hex digits", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_values_set_nothing_so_appsettings_still_applies(string empty)
    {
        var config = Map(new()
        {
            ["Port"] = empty,
            ["CertificateThumbprint"] = empty,
            ["AdminGroups"] = empty,
            ["ViewerGroups"] = empty,
        }, admins: 2, viewers: 2);

        Assert.Empty(config);
    }

    [Fact]
    public void Group_list_drops_blank_entries_and_trims()
    {
        Assert.Equal([@"CONTOSO\A", @".\B"], InstallerSettings.SplitGroups(@" CONTOSO\A ;; .\B ; "));
    }

    [Fact]
    public void Multi_string_group_list_is_accepted()
    {
        var config = Map(new() { ["AdminGroups"] = new[] { @"CONTOSO\A", "", @".\B" } });
        Assert.Equal(@"CONTOSO\A", config["Yanmt:Roles:AdminGroups:0"]);
        Assert.Equal(@".\B", config["Yanmt:Roles:AdminGroups:1"]);
        Assert.False(config.ContainsKey("Yanmt:Roles:AdminGroups:2"));
    }

    [Fact]
    public void Shorter_list_hides_the_extra_entries_of_a_longer_list_below_it()
    {
        // appsettings.json has three admin groups; the installer set one. The extra indexes repeat it.
        var config = Map(new() { ["AdminGroups"] = @"CONTOSO\YANMT-Admins" }, admins: 3);

        Assert.Equal(@"CONTOSO\YANMT-Admins", config["Yanmt:Roles:AdminGroups:0"]);
        Assert.Equal(@"CONTOSO\YANMT-Admins", config["Yanmt:Roles:AdminGroups:1"]);
        Assert.Equal(@"CONTOSO\YANMT-Admins", config["Yanmt:Roles:AdminGroups:2"]);
        Assert.False(config.ContainsKey("Yanmt:Roles:AdminGroups:3"));
    }

    [Fact]
    public void Unset_list_leaves_the_lower_layer_alone() =>
        Assert.DoesNotContain(Map(new() { ["AdminGroups"] = @"CONTOSO\A" }, viewers: 2).Keys, k => k.Contains("ViewerGroups"));

    [Fact]
    public void Unsupported_registry_type_fails() =>
        Assert.Throws<InvalidOperationException>(() => Map(new() { ["AdminGroups"] = new byte[] { 1 } }));
}
