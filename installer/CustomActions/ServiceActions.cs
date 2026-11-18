using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;
using Microsoft.Win32;
using WixToolset.Dtf.WindowsInstaller;

namespace Yanmt.Installer.CustomActions;

/// <summary>The YANMT service: replacing a manual install's service, the pre-upgrade backup, and starting it.</summary>
public static class ServiceActions
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);
    private const int KeptBackups = 3;

    /// <summary>
    /// Deferred, fresh installs only (this MSI isn't installed and isn't upgrading itself). A YANMT service
    /// that exists now was created by hand (DEPLOYMENT.md "Manual install", usually as svc-yanmt): stop it,
    /// wait, and delete it before InstallServices creates the MSI's own. The svc-yanmt account is left alone.
    /// Scheduled only when the YanmtService component (and so its ServiceInstall) will be installed, and
    /// PrepareInstall has already refused the install otherwise: never leave the machine with no service (issue #47).
    /// </summary>
    [CustomAction]
    public static ActionResult RemoveLegacyService(Session session)
    {
        if (!Exists())
        {
            session.LogRaw("YANMT: no existing YANMT service.");
            return ActionResult.Success;
        }

        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceIdentity.ServiceName))
        {
            session.LogRaw($"YANMT: replacing a YANMT service this MSI didn't install: runs as '{key?.GetValue("ObjectName")}', " +
                $"binary '{key?.GetValue("ImagePath")}'. Its account is not deleted.");
        }

        if (!Stop(session))
        {
            return Fail(session, "The existing YANMT service did not stop within 2 minutes. Stop it (Stop-Service YANMT), then run the installer again.");
        }

        Delete();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Exists())
        {
            if (DateTime.UtcNow > deadline)
            {
                return Fail(session, "The existing YANMT service is marked for deletion but still exists, usually because services.msc or " +
                    "another tool has it open. Close it, then run the installer again.");
            }

            Thread.Sleep(500);
        }

        session.LogRaw("YANMT: deleted the old YANMT service.");
        return ActionResult.Success;
    }

    /// <summary>
    /// Deferred, right after InstallServices, on install, upgrade, change and repair. The YANMT service must
    /// exist, run as NT SERVICE\YANMT and point at the installed yanmt.exe. Otherwise the install fails and
    /// rolls back instead of reporting success with no service (issue #47: a skipped YanmtService component).
    /// </summary>
    [CustomAction]
    public static ActionResult VerifyYanmtService(Session session)
    {
        var expected = Path.Combine(session.CustomActionData["InstallFolder"], "yanmt.exe");
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceIdentity.ServiceName);
        if (!Exists() || key is null)
        {
            return Fail(session, "The YANMT service was not created, so the installation is rolled back. Search the install log " +
                "for \"YANMT\" and \"Disallowing installation of component\". A manual install's service that this installer " +
                "already deleted is not restored: run the installer again once the cause is fixed, or recreate it (DEPLOYMENT.md section 3.6).");
        }

        var account = key.GetValue("ObjectName") as string ?? "";
        var image = (key.GetValue("ImagePath", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "").Trim().Trim('"');
        if (!string.Equals(account, ServiceIdentity.Account, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFullPath(image), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
        {
            return Fail(session, $"The YANMT service is not the one this installer creates: it runs as '{account}' from '{image}', " +
                $"expected '{ServiceIdentity.Account}' from '{expected}'. The installation is rolled back.");
        }

        session.LogRaw($"YANMT: the YANMT service exists, runs as {account} from {image}.");
        return ActionResult.Success;
    }

    /// <summary>
    /// Deferred, upgrades only, right after RemoveExistingProducts (the old version is gone, the new files
    /// aren't there yet, so no migration has run). Makes sure the service is stopped and copies yanmt.db (with
    /// -wal/-shm) and secrets.entropy to backups\pre-upgrade\&lt;old version&gt;-&lt;timestamp&gt;. Keeps the newest 3.
    /// A failed copy fails the upgrade (it rolls back): migrations are one-way.
    /// </summary>
    [CustomAction]
    public static ActionResult BackupBeforeUpgrade(Session session)
    {
        var data = session.CustomActionData;
        var dataFolder = data["DataFolder"];
        if (Exists() && !Stop(session))
        {
            return Fail(session, "The YANMT service did not stop within 2 minutes, so the database can't be backed up before the upgrade.");
        }

        var files = new[] { "yanmt.db", "yanmt.db-wal", "yanmt.db-shm", "secrets.entropy" }
            .Select(f => Path.Combine(dataFolder, f)).Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            session.LogRaw($"YANMT: no database in {dataFolder}; nothing to back up before the upgrade.");
            return ActionResult.Success;
        }

        var root = Path.Combine(dataFolder, "backups", "pre-upgrade");
        var target = Path.Combine(root, $"{Safe(data["OldVersion"])}-{DateTime.Now:yyyyMMdd-HHmmss}");
        try
        {
            Directory.CreateDirectory(target);
            foreach (var file in files)
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return Fail(session, $"Could not back up the database to {target} before the upgrade: {ex.Message}");
        }

        session.LogRaw($"YANMT: backed up {string.Join(", ", files.Select(Path.GetFileName))} to {target}.");
        foreach (var old in new DirectoryInfo(root).GetDirectories().OrderByDescending(d => d.CreationTimeUtc).Skip(KeptBackups))
        {
            try
            {
                old.Delete(recursive: true);
                session.LogRaw($"YANMT: removed old pre-upgrade backup {old.FullName}.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                session.LogRaw($"YANMT: could not remove old pre-upgrade backup {old.FullName}: {ex.Message}");
            }
        }

        return ActionResult.Success;
    }

    /// <summary>
    /// Deferred, last before InstallFinalize. Starts the service, but never fails the install: a service
    /// that won't start (bad certificate, port in use) is reported, and the user is pointed at the logs.
    /// </summary>
    [CustomAction]
    public static ActionResult StartYanmtService(Session session)
    {
        var data = session.CustomActionData;
        try
        {
            using var service = new ServiceController(ServiceIdentity.ServiceName);
            if (service.Status != ServiceControllerStatus.Running)
            {
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, StartTimeout);
            }

            session.LogRaw($"YANMT: the service is running. Sign in at {data["Url"]}");
        }
        catch (Exception ex)
        {
            // Anything at all: this action must not roll the install back (issue #40, 2.6).
            session.LogRaw($"YANMT WARNING: the installation finished, but the YANMT service did not start ({ex.Message}). " +
                $"See {Path.Combine(data["DataFolder"], "logs", "startup-failure.log")} and DEPLOYMENT.md, Troubleshooting.");
        }

        // Logged here, once ("YANMT WARNING: firewall: ..."); the final dialog checks again without logging (ReportServiceState).
        FirewallCheck.Check(session, int.Parse(data["Port"], System.Globalization.CultureInfo.InvariantCulture), data["FirewallRule"] == "1");
        return ActionResult.Success;
    }

    /// <summary>
    /// Immediate, UI sequence after ExecuteAction: sets the final dialog's text, which always says whether
    /// the service is running, plus a firewall warning if the active profile isn't covered. Reads state only,
    /// which needs no elevation.
    /// </summary>
    [CustomAction]
    public static ActionResult ReportServiceState(Session session)
    {
        string text;
        try
        {
            using var service = new ServiceController(ServiceIdentity.ServiceName);
            ServiceControllerStatus? status;
            try
            {
                status = service.Status;
            }
            catch (InvalidOperationException)
            {
                status = null;   // No such service.
            }

            text = status switch
            {
                ServiceControllerStatus.Running => $"The YANMT service is running. Sign in at https://{HostName()}:{session["PORT"]}/",
                null => "The YANMT service is NOT installed. See the installation log.",
                _ => $"The YANMT service is installed but NOT running ({status}). The reason is in " +
                     Path.Combine(session["DATAFOLDER"], "logs", "startup-failure.log") + ".",
            };
        }
        catch (Exception ex)
        {
            session.LogRaw("YANMT: could not read the service state: " + ex.Message);
            text = "Could not read the state of the YANMT service: " + ex.Message;
        }

        var port = int.TryParse(session["PORT"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 0;
        // StartYanmtService already logged the firewall state; this is for the dialog only (issue #49, M7).
        if (FirewallCheck.Check(session, port, !string.IsNullOrEmpty(session["FIREWALL_REMOTE_ADDRESS"]), log: false) is { } warning)
        {
            text += "\r\n\r\nFirewall: " + warning;
        }

        session["WIXUI_EXITDIALOGOPTIONALTEXT"] = text;
        return ActionResult.Success;
    }

    private static string HostName()
    {
        try
        {
            return System.Net.Dns.GetHostEntry(Environment.MachineName).HostName;
        }
        catch (Exception)
        {
            return Environment.MachineName;
        }
    }

    private static bool Exists() =>
        ServiceController.GetServices().Any(s =>
        {
            using (s)
            {
                return string.Equals(s.ServiceName, ServiceIdentity.ServiceName, StringComparison.OrdinalIgnoreCase);
            }
        });

    private static bool Stop(Session session)
    {
        using var service = new ServiceController(ServiceIdentity.ServiceName);
        try
        {
            if (service.Status == ServiceControllerStatus.Stopped)
            {
                return true;
            }

            if (service.Status != ServiceControllerStatus.StopPending)
            {
                service.Stop();
            }

            service.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout);
            session.LogRaw("YANMT: stopped the YANMT service.");
            return true;
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            return false;
        }
    }

    private static void Delete()
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            throw new Win32Exception();
        }

        try
        {
            var service = OpenService(manager, ServiceIdentity.ServiceName, DeleteAccess);
            if (service == IntPtr.Zero)
            {
                throw new Win32Exception();
            }

            try
            {
                // ERROR_SERVICE_MARKED_FOR_DELETE (1072) is fine: the wait in the caller covers it.
                if (!DeleteService(service) && Marshal.GetLastWin32Error() != 1072)
                {
                    throw new Win32Exception();
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static string Safe(string version) =>
        new string((version ?? "").Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_').ToArray());

    private static ActionResult Fail(Session session, string message)
    {
        session.LogRaw("YANMT: " + message);
        SettingsActions.Fail(session, message);
        return ActionResult.Failure;
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint DeleteAccess = 0x10000;

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
