namespace Yanmt.Core.Security;

/// <summary>
/// Rules for the ACL of <c>secrets.entropy</c> and the messages shown when the service can't use it.
/// The file is created with the data folder's inheritable ACL (so whoever the folder grants, the
/// service account included, can read it) minus any broad principal, plus SYSTEM and Administrators.
/// </summary>
public static class EntropyFileRules
{
    // Everyone, LOCAL, CONSOLE LOGON, DIALUP, NETWORK, BATCH, INTERACTIVE, ANONYMOUS LOGON,
    // Authenticated Users, TERMINAL SERVER USER, REMOTE INTERACTIVE LOGON, This Organization,
    // BUILTIN\Users, Guests, Power Users, Remote Desktop Users, ALL (RESTRICTED) APPLICATION PACKAGES.
    private static readonly HashSet<string> BroadSids = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-1-0", "S-1-2-0", "S-1-2-1",
        "S-1-5-1", "S-1-5-2", "S-1-5-3", "S-1-5-4", "S-1-5-7", "S-1-5-11", "S-1-5-13", "S-1-5-14", "S-1-5-15",
        "S-1-5-32-545", "S-1-5-32-546", "S-1-5-32-547", "S-1-5-32-555",
        "S-1-15-2-1", "S-1-15-2-2",
    };

    /// <summary>CREATOR OWNER: on an inherited ACE it means "whoever creates the file".</summary>
    public const string CreatorOwnerSid = "S-1-3-0";

    /// <summary>
    /// True for groups that cover ordinary users (Users, Everyone, Authenticated Users, Domain Users...).
    /// Allow entries for these are never copied onto the entropy file.
    /// </summary>
    public static bool IsBroadPrincipal(string sid)
    {
        if (BroadSids.Contains(sid))
        {
            return true;
        }

        // Domain Users (513), Domain Guests (514), Domain Computers (515): S-1-5-21-<domain>-<rid>.
        if (sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase))
        {
            var rid = sid[(sid.LastIndexOf('-') + 1)..];
            return rid is "513" or "514" or "515";
        }

        return false;
    }

    /// <summary>
    /// Folder ACEs can hold generic rights (ProgramData's CREATOR OWNER entry is GENERIC_ALL). Windows
    /// maps those to file rights when a file inherits them; an explicit ACE must carry the mapped form,
    /// because access checks ignore unmapped generic bits.
    /// </summary>
    public static int MapGenericFileRights(int accessMask)
    {
        const int genericRead = unchecked((int)0x80000000), genericWrite = 0x40000000, genericExecute = 0x20000000, genericAll = 0x10000000;
        const int fileGenericRead = 0x120089, fileGenericWrite = 0x120116, fileGenericExecute = 0x1200A0, fileAllAccess = 0x1F01FF;

        var mapped = accessMask & ~(genericRead | genericWrite | genericExecute | genericAll);
        foreach (var (generic, specific) in new[]
                 {
                     (genericRead, fileGenericRead), (genericWrite, fileGenericWrite),
                     (genericExecute, fileGenericExecute), (genericAll, fileAllAccess),
                 })
        {
            if ((accessMask & generic) != 0)
            {
                mapped |= specific;
            }
        }

        return mapped;
    }

    /// <summary>The file exists but this account can't read it (created by a hand run, restore, or migration).</summary>
    /// <param name="savedCredentialProfiles">Credential profiles in the database, or null if unknown.</param>
    public static string UnreadableMessage(string path, string account, int? savedCredentialProfiles)
    {
        var message =
            $"Cannot read {path} as {account}. The file exists, but this account has no read access to it " +
            "(it was probably created by a hand run, a restore, or a migration under another account). " +
            $"Fix, as an administrator: icacls \"{path}\" /grant \"{account}:R\" and then start the service.";
        return savedCredentialProfiles switch
        {
            0 => message + " No device credentials are saved yet, so you can delete the file instead; the service recreates it with the right ACL.",
            > 0 => message + $" Do not delete it: {savedCredentialProfiles} saved credential profile(s) need it to decrypt.",
            _ => message + " Do not delete it if any device credentials are saved: they can't be decrypted without it.",
        };
    }

    /// <summary>The file doesn't exist and this account can't create it in the data folder.</summary>
    public static string CannotCreateMessage(string path, string dataFolder, string account) =>
        $"Cannot create {path} as {account}. The account needs Modify on the data folder. " +
        $"Fix, as an administrator: icacls \"{dataFolder}\" /grant \"{account}:(OI)(CI)M\" /T and then start the service.";
}
