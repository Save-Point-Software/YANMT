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
    /// </summary>
    [CustomAction]
    public static ActionResult RemoveLegacyService(Session session)
    {
        if (!Exists())
        {
            session.Log("YANMT: no existing YANMT service.");
            return ActionResult.Success;
        }

        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceIdentity.ServiceName))
        {
            session.Log($"YANMT: replacing a YANMT service this MSI didn't install: runs as '{key?.GetValue("ObjectName")}', " +
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

        session.Log("YANMT: deleted the old YANMT service.");
        return ActionResult.Success;
    }

    /// <summary>
    /// Deferred, upgrades only, before RemoveExistingProducts. Stops the service and copies yanmt.db (with
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
            session.Log($"YANMT: no database in {dataFolder}; nothing to back up before the upgrade.");
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

        session.Log($"YANMT: backed up {string.Join(", ", files.Select(Path.GetFileName))} to {target}.");
        foreach (var old in new DirectoryInfo(root).GetDirectories().OrderByDescending(d => d.CreationTimeUtc).Skip(KeptBackups))
        {
            try
            {
                old.Delete(recursive: true);
                session.Log($"YANMT: removed old pre-upgrade backup {old.FullName}.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                session.Log($"YANMT: could not remove old pre-upgrade backup {old.FullName}: {ex.Message}");
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

            session.Log($"YANMT: the service is running. Sign in at {data["Url"]}");
        }
        catch (Exception ex)
        {
            // Anything at all: this action must not roll the install back (issue #40, 2.6).
            session.Log($"YANMT WARNING: the installation finished, but the YANMT service did not start ({ex.Message}). " +
                $"See {Path.Combine(data["DataFolder"], "logs", "startup-failure.log")} and DEPLOYMENT.md, Troubleshooting.");
        }

        return ActionResult.Success;
    }

    /// <summary>
    /// Immediate, UI sequence after ExecuteAction: sets the final dialog's text. Reads the service state
    /// only, which needs no elevation.
    /// </summary>
    [CustomAction]
    public static ActionResult ReportServiceState(Session session)
    {
        try
        {
            var url = $"https://{System.Net.Dns.GetHostEntry(Environment.MachineName).HostName}:{session["PORT"]}/";
            using var service = new ServiceController(ServiceIdentity.ServiceName);
            session["WIXUI_EXITDIALOGOPTIONALTEXT"] = service.Status == ServiceControllerStatus.Running
                ? $"The YANMT service is running. Sign in at {url}"
                : "The YANMT service did not start. The reason is in " +
                  Path.Combine(session["DATAFOLDER"], "logs", "startup-failure.log") + ".";
        }
        catch (Exception ex)
        {
            session.Log("YANMT: could not read the service state: " + ex.Message);
        }

        return ActionResult.Success;
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
            session.Log("YANMT: stopped the YANMT service.");
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
        session.Log("YANMT: " + message);
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
