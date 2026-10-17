using System.Security.Claims;
using System.Security.Principal;
using Yanmt.Infrastructure.Security;

namespace Yanmt.Infrastructure.Tests.Security;

public class RoleMappingTests
{
    private const string Everyone = "S-1-1-0";              // present in every Windows token
    private const string BuiltinUsers = "S-1-5-32-545";
    private const string NeverPresent = "S-1-5-21-1-2-3-4242"; // made-up domain SID

    [Theory]
    [InlineData(@"BUILTIN\Users")]
    [InlineData("Users")]               // local group by bare name
    [InlineData(BuiltinUsers)]          // raw SID
    public void Resolves_group_names_and_sids(string configured)
    {
        var sid = RoleGroupMap.TryResolve(configured, out var error);
        Assert.Null(error);
        Assert.Equal(new SecurityIdentifier(BuiltinUsers), sid);
    }

    [Fact]
    public void Resolves_dot_prefix_as_a_local_account_on_this_machine()
    {
        // ".\name" must mean MACHINE\name. Use the current user when it is a local account (true on CI runners).
        Assert.SkipUnless(
            string.Equals(Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase),
            "Current user is not a local account.");

        var sid = RoleGroupMap.TryResolve($@".\{Environment.UserName}", out var error);

        Assert.Null(error);
        Assert.Equal(WindowsIdentity.GetCurrent().User, sid);
    }

    [Fact]
    public void Unknown_names_are_reported_not_thrown()
    {
        var map = RoleGroupMap.Resolve(new RoleGroupOptions
        {
            AdminGroups = [@"NOSUCHDOMAIN\No-Such-Group-7f3a"],
            ViewerGroups = [Everyone],
        });

        Assert.Empty(map.AdminSids);
        Assert.Single(map.ViewerSids);
        Assert.Contains(map.Problems, p => p.Contains("No-Such-Group-7f3a"));
        Assert.Contains(map.Problems, p => p.Contains("No admin groups"));
    }

    [Fact]
    public void Empty_configuration_is_reported()
    {
        var map = RoleGroupMap.Resolve(new RoleGroupOptions());
        Assert.Contains(map.Problems, p => p.Contains("nobody will be able to use YANMT"));
    }

    [Fact]
    public void RolesFor_maps_token_sids_to_roles()
    {
        var transform = Transform(admin: [BuiltinUsers], viewer: [Everyone]);
        HashSet<SecurityIdentifier> Sids(params string[] s) => s.Select(x => new SecurityIdentifier(x)).ToHashSet();

        Assert.Equal([YanmtRoles.Admin, YanmtRoles.Viewer], transform.RolesFor(Sids(Everyone, BuiltinUsers)));
        Assert.Equal([YanmtRoles.Viewer], transform.RolesFor(Sids(Everyone)));
        Assert.Empty(transform.RolesFor(Sids(NeverPresent)));
    }

    [Fact]
    public async Task Transformation_adds_roles_to_the_current_windows_user()
    {
        var transform = Transform(admin: [NeverPresent], viewer: [Everyone]);
        var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());

        var result = await transform.TransformAsync(principal);

        Assert.True(result.IsInRole(YanmtRoles.Viewer));
        Assert.False(result.IsInRole(YanmtRoles.Admin));
        Assert.Equal(principal.Identity.Name, result.Identity!.Name);
    }

    [Fact]
    public async Task Transformation_is_idempotent()
    {
        var transform = Transform(admin: [Everyone], viewer: []);
        var once = await transform.TransformAsync(new WindowsPrincipal(WindowsIdentity.GetCurrent()));
        var twice = await transform.TransformAsync(once);

        Assert.Same(once, twice);
        Assert.Single(twice.FindAll(ClaimTypes.Role));
    }

    [Fact]
    public async Task Non_windows_principals_are_untouched()
    {
        var transform = Transform(admin: [Everyone], viewer: [Everyone]);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "x")], "Other"));

        var result = await transform.TransformAsync(principal);

        Assert.Same(principal, result);
        Assert.False(result.IsInRole(YanmtRoles.Admin));
    }

    private static GroupRoleClaimsTransformation Transform(string[] admin, string[] viewer) =>
        new(RoleGroupMap.Resolve(new RoleGroupOptions { AdminGroups = [.. admin], ViewerGroups = [.. viewer] }));
}
