using System;
using System.DirectoryServices.AccountManagement;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using WixToolset.Dtf.WindowsInstaller;
using FileAttributes = System.IO.FileAttributes;

namespace Yanmt.Installer.CustomActions;

/// <summary>Deferred, as LocalSystem (Impersonate="no"): local groups and folder permissions.</summary>
public static class MachineActions
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);

    /// <summary>Creates the missing local groups (".\Name") from ADMIN_GROUPS and VIEWER_GROUPS. Never adds members.</summary>
    [CustomAction]
    public static ActionResult CreateLocalGroups(Session session)
    {
        var lines = (session.CustomActionData["Groups"] ?? "").Split('\n').Where(l => l.Length > 0).ToList();
        if (lines.Count == 0)
        {
            return ActionResult.Success;
        }

        using var context = new PrincipalContext(ContextType.Machine);
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            var isAdmin = line.Substring(0, colon) == "admin";
            var name = line.Substring(colon + 1);
            using var existing = GroupPrincipal.FindByIdentity(context, IdentityType.SamAccountName, name);
            if (existing != null)
            {
                session.Log($"YANMT: local group {name} already exists.");
                continue;
            }

            using var group = new GroupPrincipal(context, name)
            {
                Description = isAdmin ? "YANMT administrators" : "YANMT read-only users",
            };
            group.Save();
            session.Log($"YANMT: created local group {Environment.MachineName}\\{name} (no members; add them yourself).");
        }

        return ActionResult.Success;
    }

    /// <summary>
    /// Install folder: read and execute for NT SERVICE\YANMT, full control for Administrators and SYSTEM.
    /// Data folder: modify for the service, full control for Administrators and SYSTEM. Both protected (nothing
    /// inherited from Program Files or ProgramData). Everything already inside goes back to inheriting only, so
    /// grants from a manual install (such as svc-yanmt) are removed: the /T of DEPLOYMENT.md, and stricter.
    /// secrets.entropy keeps its own protected ACL, as the app creates it.
    /// </summary>
    [CustomAction]
    public static ActionResult SetFolderAcls(Session session)
    {
        var data = session.CustomActionData;
        Secure(session, data["InstallFolder"], FileSystemRights.ReadAndExecute);
        var dataFolder = data["DataFolder"];
        Secure(session, dataFolder, FileSystemRights.Modify);

        var entropy = Path.Combine(dataFolder, "secrets.entropy");
        if (File.Exists(entropy))
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(ServiceIdentity.Sid, FileSystemRights.Modify, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, AccessControlType.Allow));
            File.SetAccessControl(entropy, security);
            session.Log($"YANMT: reset the ACL of {entropy}.");
        }

        return ActionResult.Success;
    }

    private static void Secure(Session session, string folder, FileSystemRights serviceRights)
    {
        folder = folder.TrimEnd('\\');
        Directory.CreateDirectory(folder);
        const InheritanceFlags both = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(ServiceIdentity.Sid, serviceRights, both, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
        Directory.SetAccessControl(folder, security);

        var reset = 0;
        foreach (var child in Children(folder))
        {
            if (child.Attributes.HasFlag(FileAttributes.Directory))
            {
                var inherit = new DirectorySecurity();
                inherit.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                Directory.SetAccessControl(child.FullName, inherit);
            }
            else
            {
                var inherit = new FileSecurity();
                inherit.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                File.SetAccessControl(child.FullName, inherit);
            }

            reset++;
        }

        session.Log($"YANMT: {folder}: {ServiceIdentity.Account} ({ServiceIdentity.Sid.Value}) {serviceRights}, " +
            $"Administrators and SYSTEM full control, not inherited; {reset} existing entries reset to inherit only.");
    }

    /// <summary>Every file and folder below <paramref name="folder"/>, not following junctions or symbolic links.</summary>
    private static System.Collections.Generic.IEnumerable<FileSystemInfo> Children(string folder)
    {
        var pending = new System.Collections.Generic.Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(folder));
        while (pending.Count > 0)
        {
            foreach (var entry in pending.Pop().EnumerateFileSystemInfos())
            {
                yield return entry;
                if (entry is DirectoryInfo dir && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    pending.Push(dir);
                }
            }
        }
    }

    /// <summary>
    /// Real uninstall only (not the old product's removal during an upgrade), after RemoveFiles.
    /// appsettings.json is a permanent component so upgrades keep a hand-edited copy: delete it now.
    /// Then delete empty subfolders, which Windows Installer leaves when they existed before the MSI
    /// (a manual install), and the install folder itself if it's empty. Anything still holding a file is left alone.
    /// </summary>
    [CustomAction]
    public static ActionResult CleanInstallFolder(Session session)
    {
        var folder = session.CustomActionData["InstallFolder"];
        try
        {
            var settings = Path.Combine(folder, "appsettings.json");
            if (File.Exists(settings))
            {
                File.Delete(settings);
                session.Log($"YANMT: removed {settings}.");
            }

            if (Directory.Exists(folder))
            {
                foreach (var dir in Directory.GetDirectories(folder, "*", SearchOption.AllDirectories)
                             .OrderByDescending(d => d.Length))
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                    }
                }

                if (!Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    Directory.Delete(folder);
                    session.Log($"YANMT: removed the empty {folder}.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            session.Log($"YANMT: could not clean up {folder}: {ex.Message}");
        }

        return ActionResult.Success;
    }
}
