using System.Security.Claims;
using System.Security.Principal;
using Microsoft.AspNetCore.Authentication;

namespace Yanmt.Infrastructure.Security;

/// <summary>
/// Adds YANMT role claims to a Windows-authenticated user based on the SIDs in their token
/// (user SID plus every group, AD or local). Runs on every authenticated request, so it is
/// idempotent and cheap: a set intersection, no directory lookups.
/// </summary>
public sealed class GroupRoleClaimsTransformation(RoleGroupMap map) : IClaimsTransformation
{
    private const string MarkerClaimType = "yanmt:roles-mapped";

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not WindowsIdentity { IsAuthenticated: true } windows
            || principal.HasClaim(c => c.Type == MarkerClaimType))
        {
            return Task.FromResult(principal);
        }

        var tokenSids = new HashSet<SecurityIdentifier>();
        if (windows.User is not null)
        {
            tokenSids.Add(windows.User);
        }

        foreach (var group in windows.Groups ?? [])
        {
            if (group is SecurityIdentifier sid)
            {
                tokenSids.Add(sid);
            }
        }

        var roles = RolesFor(tokenSids);
        var identity = new ClaimsIdentity(authenticationType: null, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(MarkerClaimType, "1"));
        foreach (var role in roles)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        var result = principal.Clone();
        result.AddIdentity(identity);
        return Task.FromResult(result);
    }

    /// <summary>Admins are also viewers; the Viewer policy accepts either role.</summary>
    public IReadOnlyList<string> RolesFor(IReadOnlySet<SecurityIdentifier> tokenSids)
    {
        var roles = new List<string>(2);
        if (map.AdminSids.Overlaps(tokenSids))
        {
            roles.Add(YanmtRoles.Admin);
        }

        if (map.ViewerSids.Overlaps(tokenSids))
        {
            roles.Add(YanmtRoles.Viewer);
        }

        return roles;
    }
}
