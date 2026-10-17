using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace Yanmt.Infrastructure.Security;

/// <summary>Bound from "Yanmt:Roles". Each entry is an AD group, a local group, or a SID.</summary>
public sealed class RoleGroupOptions
{
    public const string SectionName = "Yanmt:Roles";

    /// <summary>
    /// Examples: "CONTOSO\YANMT-Admins" (AD), "YANMT-Admins" or ".\YANMT-Admins" or "SERVER01\YANMT-Admins"
    /// (local group on this server), or a raw SID such as "S-1-5-21-...".
    /// </summary>
    public List<string> AdminGroups { get; set; } = [];

    public List<string> ViewerGroups { get; set; } = [];
}

/// <summary>
/// The configured group names resolved to SIDs once at startup. Matching by SID means AD
/// and local groups take the same code path, and group renames don't silently break access.
/// </summary>
public sealed class RoleGroupMap
{
    private RoleGroupMap(IReadOnlySet<SecurityIdentifier> admin, IReadOnlySet<SecurityIdentifier> viewer, IReadOnlyList<string> problems)
    {
        AdminSids = admin;
        ViewerSids = viewer;
        Problems = problems;
    }

    public IReadOnlySet<SecurityIdentifier> AdminSids { get; }
    public IReadOnlySet<SecurityIdentifier> ViewerSids { get; }

    /// <summary>Names that could not be resolved, or other configuration problems. Logged at startup.</summary>
    public IReadOnlyList<string> Problems { get; }

    public static RoleGroupMap Resolve(RoleGroupOptions options, ILogger? logger = null)
    {
        var problems = new List<string>();
        var admin = ResolveAll(options.AdminGroups, YanmtRoles.Admin, problems, logger);
        var viewer = ResolveAll(options.ViewerGroups, YanmtRoles.Viewer, problems, logger);

        if (admin.Count == 0 && viewer.Count == 0)
        {
            problems.Add("No admin or viewer groups resolved; nobody will be able to use YANMT. Set Yanmt:Roles in appsettings.json.");
        }
        else if (admin.Count == 0)
        {
            problems.Add("No admin groups resolved; nobody will be able to change configuration.");
        }

        foreach (var problem in problems)
        {
            logger?.LogError("Role mapping: {Problem}", problem);
        }

        return new RoleGroupMap(admin, viewer, problems);
    }

    /// <summary>Translates one configured name to a SID. Returns null (with an error message) if it doesn't resolve.</summary>
    public static SecurityIdentifier? TryResolve(string configured, out string? error)
    {
        error = null;
        var name = configured.Trim();
        if (name.Length == 0)
        {
            error = "an empty group name";
            return null;
        }

        try
        {
            if (name.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
            {
                return new SecurityIdentifier(name);
            }

            // ".\Group" means a local group on this machine.
            if (name.StartsWith(@".\", StringComparison.Ordinal))
            {
                name = Environment.MachineName + name[1..];
            }

            return (SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier));
        }
        catch (Exception ex)
        {
            // Translate fails in several ways: IdentityNotMappedException (no such account),
            // ArgumentException (malformed name or SID), SystemException (domain trust failure).
            // Any of them means "this entry grants nothing", never a startup crash.
            error = $"'{configured}' could not be resolved to a Windows account or group ({ex.Message.TrimEnd('.')})";
            return null;
        }
    }

    private static HashSet<SecurityIdentifier> ResolveAll(IEnumerable<string> names, string role, List<string> problems, ILogger? logger)
    {
        var sids = new HashSet<SecurityIdentifier>();
        foreach (var configured in names)
        {
            var sid = TryResolve(configured, out var error);
            if (sid is null)
            {
                problems.Add($"{role} group {error}.");
                continue;
            }

            sids.Add(sid);
            logger?.LogInformation("Role mapping: {Role} <- {Group} ({Sid})", role, configured, sid.Value);
        }

        return sids;
    }
}
