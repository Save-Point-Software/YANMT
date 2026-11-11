using Yanmt.Core.Security;

namespace Yanmt.Core.Tests.Security;

public class EntropyFileRulesTests
{
    [Theory]
    [InlineData("S-1-1-0")]          // Everyone
    [InlineData("S-1-5-11")]         // Authenticated Users
    [InlineData("S-1-5-32-545")]     // BUILTIN\Users
    [InlineData("S-1-5-4")]          // INTERACTIVE
    [InlineData("S-1-15-2-1")]       // ALL APPLICATION PACKAGES
    [InlineData("S-1-5-21-1111111111-2222222222-3333333333-513")] // Domain Users
    public void Broad_groups_are_never_copied(string sid) => Assert.True(EntropyFileRules.IsBroadPrincipal(sid));

    [Theory]
    [InlineData("S-1-5-18")]         // SYSTEM
    [InlineData("S-1-5-32-544")]     // Administrators
    [InlineData("S-1-5-20")]         // NETWORK SERVICE
    [InlineData("S-1-5-21-1111111111-2222222222-3333333333-1001")] // a local account such as svc-yanmt
    [InlineData("S-1-5-21-1111111111-2222222222-3333333333-5130")] // RID that only starts with 513
    public void Specific_accounts_are_kept(string sid) => Assert.False(EntropyFileRules.IsBroadPrincipal(sid));

    [Fact]
    public void Generic_rights_map_to_file_rights()
    {
        Assert.Equal(0x1F01FF, EntropyFileRules.MapGenericFileRights(0x10000000));               // GENERIC_ALL -> full control
        Assert.Equal(0x1200A9, EntropyFileRules.MapGenericFileRights(unchecked((int)0xA0000000))); // GENERIC_READ|EXECUTE -> RX
        Assert.Equal(0x1301BF, EntropyFileRules.MapGenericFileRights(0x1301BF));                // Modify, already specific
    }

    [Fact]
    public void Unreadable_message_names_the_account_and_the_exact_icacls_command()
    {
        var message = EntropyFileRules.UnreadableMessage(@"C:\ProgramData\YANMT\secrets.entropy", @"YANMT01\svc-yanmt", new EntropyKeyUsage(0, 0));

        Assert.Contains(@"as YANMT01\svc-yanmt", message);
        Assert.Contains(@"icacls ""C:\ProgramData\YANMT\secrets.entropy"" /grant ""YANMT01\svc-yanmt:R""", message);
        Assert.Contains("delete the file", message);
    }

    [Fact]
    public void Retained_config_history_without_credential_profiles_still_forbids_deleting_the_key()
    {
        // An admin can delete the last credential profile and keep the node's config history.
        var message = EntropyFileRules.UnreadableMessage("p", "a", new EntropyKeyUsage(CredentialProfiles: 0, ConfigVersions: 14));

        Assert.Contains("Do not delete it: 14 config backup version(s) need it", message);
        Assert.DoesNotContain("delete the file", message);
    }

    [Fact]
    public void Unreadable_message_only_suggests_deleting_when_nothing_is_encrypted_with_the_key()
    {
        var saved = EntropyFileRules.UnreadableMessage("p", "a", new EntropyKeyUsage(3, 0));
        Assert.Contains("Do not delete it: 3 saved credential profile(s) need it", saved);
        Assert.DoesNotContain("delete the file", saved);

        var both = EntropyFileRules.UnreadableMessage("p", "a", new EntropyKeyUsage(2, 5));
        Assert.Contains("2 saved credential profile(s) and 5 config backup version(s)", both);

        var unknown = EntropyFileRules.UnreadableMessage("p", "a", null);
        Assert.Contains("Do not delete it if any device credentials or config backups are saved", unknown);
        Assert.DoesNotContain("delete the file", unknown);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, true)]
    [InlineData(0, 1, true)]
    public void Key_is_in_use_when_profiles_or_config_versions_exist(int profiles, int versions, bool inUse) =>
        Assert.Equal(inUse, new EntropyKeyUsage(profiles, versions).InUse);

    [Fact]
    public void Cannot_create_message_gives_the_folder_grant()
    {
        var message = EntropyFileRules.CannotCreateMessage(@"D:\YANMT\secrets.entropy", @"D:\YANMT", @"CONTOSO\svc-yanmt$");
        Assert.Contains(@"icacls ""D:\YANMT"" /grant ""CONTOSO\svc-yanmt$:(OI)(CI)M"" /T", message);
    }
}
