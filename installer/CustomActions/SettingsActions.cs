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
            // The certificate dropdown (CertificateList): a thumbprint, or the self-signed choice.
            var choice = session[CertificateList.ChoiceProperty] ?? "";
            if (choice == CertificateList.NoChoice)
            {
                choice = "";
            }
            if (choice == CertificateList.SelfSignedChoice)
            {
                session["CERT_THUMBPRINT"] = "";
                session["YANMT_CMD_CERT_THUMBPRINT"] = "";
                session["CREATE_SELF_SIGNED_CERT"] = "1";
            }
            else
            {
                session["CERT_THUMBPRINT"] = choice;
                session["CREATE_SELF_SIGNED_CERT"] = "";
            }

            var settings = InstallSettings.Read(session);
            if (choice.Length == 0)
            {
                settings.Errors.Insert(0, "Choose a certificate, or the self-signed one for a test or lab server.");
                settings.Errors.RemoveAll(e => e.StartsWith("CERT_THUMBPRINT is required", StringComparison.Ordinal));
            }

            session["YANMT_SETTINGS_ERROR"] = string.Join("\r\n", settings.Errors.Take(4));
            if (settings.Errors.Count == 0)
            {
                settings.WriteBack(session);
                session["YANMT_SETTINGS_FROM_DIALOG"] = "1";

                // An emptied optional field must survive the execute sequence's AppSearch, which reads the
                // old value back from the registry: say so explicitly (Package.wxs, YanmtClear_*).
                session["CLEAR_VIEWER_GROUPS"] = settings.ViewerGroups.Count == 0 ? "1" : "";
                session["CLEAR_FIREWALL_REMOTE_ADDRESS"] = settings.FirewallRemoteAddress.Length == 0 ? "1" : "";
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
                session.LogRaw("YANMT: " + error);
            }

            Fail(session, "YANMT can't be installed with these settings:\r\n\r\n" + string.Join("\r\n", settings.Errors));
            return ActionResult.Failure;
        }

        // Costing is done (CostFinalize), so Windows Installer has decided whether YanmtService installs. On a
        // new install or upgrade it must: if a newer yanmt.exe is already there (file versioning), the component,
        // and so the service, would be skipped silently while RemoveLegacyService deletes the old one (issue #47).
        // A legacy install gets REINSTALLMODE=amus (Package.wxs), so this is the safety net. Nothing has changed yet.
        if (!session.EvaluateCondition("Installed") && !session.EvaluateCondition("$YanmtService = 3"))
        {
            var existing = System.IO.Path.Combine(session["INSTALLFOLDER"], "yanmt.exe");
            var version = System.IO.File.Exists(existing) ? System.Diagnostics.FileVersionInfo.GetVersionInfo(existing).FileVersion : "none";
            session.LogRaw($"YANMT: the YanmtService component would not be installed (existing {existing}, file version {version}).");
            Fail(session, $"Windows Installer would not install yanmt.exe or the YANMT service: {existing} (file version {version}) " +
                "is newer than the one in this package. Nothing was changed. Remove that file (stop the service first) and run the installer again.");
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
        if (settings.CreateSelfSigned)
        {
            // The certificate doesn't exist yet: CreateSelfSignedCert makes it with this serial number, and
            // RemoveSelfSignedCert (rollback) and GrantKeyRead find it by that.
            var serial = SelfSignedCertificate.NewSerial();
            var certData = new CustomActionData { ["Serial"] = serial }.ToString();
            session["YANMT_CREATE_CERT"] = "1";
            session["CreateSelfSignedCert"] = certData;
            session["RemoveSelfSignedCert"] = certData;
            session["GrantKeyRead"] = certData;
        }
        else
        {
            session["YANMT_CREATE_CERT"] = "";
            session["GrantKeyRead"] = new CustomActionData { ["Thumbprint"] = settings.Thumbprint }.ToString();
        }

        session["StartYanmtService"] = new CustomActionData
        {
            ["Url"] = url,
            ["DataFolder"] = dataFolder,
            ["Port"] = settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["FirewallRule"] = settings.FirewallRemoteAddress.Length > 0 ? "1" : "",
        }.ToString();
        session["VerifyYanmtService"] = new CustomActionData { ["InstallFolder"] = installFolder }.ToString();

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

        foreach (var note in settings.Notes)
        {
            session.LogRaw("YANMT: " + note);
        }

        // Logged once, here, with the values this install uses: in a full-UI install this runs after the dialog.
        var source = session["YANMT_SETTINGS_FROM_DIALOG"] == "1" ? "from the settings dialog" : "from the command line and stored settings";
        var certificate = settings.CreateSelfSigned
            ? "a new self-signed one (CREATE_SELF_SIGNED_CERT=1, created later in the install)"
            : settings.Thumbprint;
        session.LogRaw($"YANMT: settings OK ({source}). Port {settings.Port}, certificate {certificate}, " +
            $"admin groups [{string.Join("; ", settings.AdminGroups.Select(g => g.Configured))}], " +
            $"viewer groups [{string.Join("; ", settings.ViewerGroups.Select(g => g.Configured))}], " +
            $"firewall remote address [{settings.FirewallRemoteAddress}]" +
            (settings.FirewallRemoteAddress.Length == 0 ? " (empty: no firewall rule is created)" : "") +
            $". Groups to create: [{string.Join("; ", settings.AllGroups.Where(g => g.CreateLocalName != null).Select(g => g.CreateLocalName))}].");
        return ActionResult.Success;
    }

    /// <summary>
    /// Immediate, UI and execute sequences, right after YanmtDetectLegacy (issue #49, M7). Both costing passes
    /// get REINSTALLMODE=amus (Package.wxs), so neither logs a component skipped for a newer key file.
    /// </summary>
    [CustomAction]
    public static ActionResult LogLegacyInstall(Session session)
    {
        session.LogRaw("YANMT: legacy install detected: replacing all files regardless of version " +
            $"(REINSTALLMODE=amus; service image [{session["YANMT_LEGACY_SERVICE"]}], exe [{session["YANMT_LEGACY_EXE"]}]).");
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
