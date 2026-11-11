using System;
using System.Linq;
using System.Net;
using WixToolset.Dtf.WindowsInstaller;

namespace Yanmt.Installer.CustomActions;

/// <summary>Immediate actions: check the settings and hand them to the deferred actions.</summary>
public static class SettingsActions
{
    /// <summary>
    /// The settings dialog's Next button. Sets YANMT_SETTINGS_ERROR to the problems (empty when valid).
    /// A DoAction from a dialog can't show a message box, so the dialog spawns YanmtInvalidDlg instead.
    /// </summary>
    [CustomAction]
    public static ActionResult ValidateSettingsUi(Session session)
    {
        try
        {
            var settings = InstallSettings.Read(session);
            session["YANMT_SETTINGS_ERROR"] = string.Join("\r\n", settings.Errors.Take(4));
            if (settings.Errors.Count == 0)
            {
                settings.WriteBack(session);
            }
        }
        catch (Exception ex)
        {
            session["YANMT_SETTINGS_ERROR"] = "Could not check the settings: " + ex.Message;
        }

        return ActionResult.Success;
    }

    /// <summary>
    /// Execute sequence, after CostFinalize, on install, repair and upgrade (not uninstall). Fails the
    /// install early, before anything changes, if a setting is wrong. Then writes CustomActionData for
    /// the deferred actions.
    /// </summary>
    [CustomAction]
    public static ActionResult PrepareInstall(Session session)
    {
        var settings = InstallSettings.Read(session);
        if (settings.Errors.Count > 0)
        {
            foreach (var error in settings.Errors)
            {
                session.Log("YANMT: " + error);
            }

            Fail(session, "YANMT can't be installed with these settings:\r\n\r\n" + string.Join("\r\n", settings.Errors));
            return ActionResult.Failure;
        }

        settings.WriteBack(session);
        var installFolder = session["INSTALLFOLDER"];
        var dataFolder = session["DATAFOLDER"];
        var url = $"https://{HostName()}:{settings.Port}/";
        session["YANMT_URL"] = url;

        session["CreateLocalGroups"] = new CustomActionData
        {
            ["Groups"] = string.Join("\n", settings.AllGroups.Where(g => g.CreateLocalName != null)
                .Select(g => (g.IsAdmin ? "admin:" : "viewer:") + g.CreateLocalName)),
        }.ToString();
        session["SetFolderAcls"] = new CustomActionData
        {
            ["InstallFolder"] = installFolder,
            ["DataFolder"] = dataFolder,
        }.ToString();
        session["GrantKeyRead"] = new CustomActionData { ["Thumbprint"] = settings.Thumbprint }.ToString();
        session["StartYanmtService"] = new CustomActionData { ["Url"] = url, ["DataFolder"] = dataFolder }.ToString();

        var upgrade = session["WIX_UPGRADE_DETECTED"];
        if (!string.IsNullOrEmpty(upgrade))
        {
            var oldCode = upgrade.Split(';')[0];
            string oldVersion;
            try
            {
                oldVersion = new ProductInstallation(oldCode).ProductVersion?.ToString() ?? "unknown";
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InstallerException)
            {
                oldVersion = "unknown";
            }

            session["BackupBeforeUpgrade"] = new CustomActionData
            {
                ["DataFolder"] = dataFolder,
                ["OldVersion"] = oldVersion,
            }.ToString();
        }

        session.Log($"YANMT: settings OK. Port {settings.Port}, certificate {settings.Thumbprint}, " +
            $"admin groups [{string.Join("; ", settings.AdminGroups.Select(g => g.Configured))}], " +
            $"viewer groups [{string.Join("; ", settings.ViewerGroups.Select(g => g.Configured))}], " +
            $"firewall remote address [{settings.FirewallRemoteAddress}]" +
            (settings.FirewallRemoteAddress.Length == 0 ? " (empty: no firewall rule is created)" : "") +
            $". Groups to create: [{string.Join("; ", settings.AllGroups.Where(g => g.CreateLocalName != null).Select(g => g.CreateLocalName))}].");
        return ActionResult.Success;
    }

    /// <summary>Shown in full UI, logged in a silent install.</summary>
    internal static void Fail(Session session, string message)
    {
        using var record = new Record(1) { FormatString = "[1]" };
        record[1] = message;
        session.Message(InstallMessage.Error | (InstallMessage)MessageButtons.OK | (InstallMessage)MessageIcon.Error, record);
    }

    private static string HostName()
    {
        try
        {
            return Dns.GetHostEntry(Environment.MachineName).HostName;
        }
        catch (Exception)
        {
            return Environment.MachineName;
        }
    }
}
