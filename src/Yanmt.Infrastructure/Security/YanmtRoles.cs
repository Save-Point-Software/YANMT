namespace Yanmt.Infrastructure.Security;

public static class YanmtRoles
{
    public const string Admin = "admin";
    public const string Viewer = "viewer";
}

public static class YanmtPolicies
{
    /// <summary>Can change inventory, maps, monitors, credentials and settings.</summary>
    public const string Admin = "Admin";

    /// <summary>Can view everything (admins are viewers too). This is also the fallback policy for every endpoint.</summary>
    public const string Viewer = "Viewer";
}
