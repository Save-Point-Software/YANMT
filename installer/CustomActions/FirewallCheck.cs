using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using WixToolset.Dtf.WindowsInstaller;

namespace Yanmt.Installer.CustomActions;

/// <summary>
/// Reads the Windows Firewall state after the install (issue #47, M6): is the web UI reachable on the
/// active profile? Read-only (INetFwPolicy2 through IDispatch); never changes a rule.
/// </summary>
internal static class FirewallCheck
{
    /// <summary>The fw:FirewallException Name in Package.wxs.</summary>
    public const string RuleName = "YANMT web UI";

    private static readonly (int Bit, string Name)[] Profiles = [(1, "Domain"), (2, "Private"), (4, "Public")];

    /// <summary>
    /// Logs the state (unless <paramref name="log"/> is false) and returns a short warning when an active profile with the firewall on has no
    /// working YANMT rule, or null when it's covered (or the state can't be read).
    /// </summary>
    public static string? Check(Session session, int port, bool ruleExpected, bool log = true)
    {
        void Log(string line)
        {
            if (log)
            {
                session.Log(line);
            }
        }

        object? policy = null;
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!;
            policy = Activator.CreateInstance(type)!;
            var active = (int)Get(policy, "CurrentProfileTypes");
            var activeNames = Names(active);
            var guarded = 0;
            foreach (var (bit, _) in Profiles)
            {
                if ((active & bit) != 0 && (bool)Get(policy, "FirewallEnabled", bit))
                {
                    guarded |= bit;
                }
            }

            if (guarded == 0)
            {
                Log($"YANMT: firewall: off for the active profile(s) {activeNames}; nothing to check.");
                return null;
            }

            string? warning;
            var modifyState = (int)Get(policy, "LocalPolicyModifyState");
            var rule = FindRule(policy);
            if (modifyState == 1)
            {
                warning = $"Group Policy ignores local firewall rules here, so allow TCP {port} in the GPO.";
            }
            else if (modifyState == 2)
            {
                warning = $"The firewall blocks all inbound connections on the {activeNames} profile (\"Block all\"), so the web UI is unreachable from other machines.";
            }
            else if (rule is null)
            {
                warning = ruleExpected
                    ? $"The firewall rule \"{RuleName}\" was not found, so the {Names(guarded)} profile may block TCP {port}."
                    : $"No YANMT firewall rule (FIREWALL_REMOTE_ADDRESS is empty). The firewall is on for the {Names(guarded)} profile, so TCP {port} is reachable only if another rule allows it.";
            }
            else
            {
                var ruleProfiles = (int)Get(rule, "Profiles");
                var enabled = (bool)Get(rule, "Enabled");
                var missing = guarded & ~ruleProfiles;
                warning = !enabled
                    ? $"The firewall rule \"{RuleName}\" is disabled."
                    : missing != 0
                        ? $"The firewall rule \"{RuleName}\" covers {Names(ruleProfiles)}, not the active {Names(missing)} profile, so the web UI is unreachable from other machines."
                        : null;
                Marshal.ReleaseComObject(rule);
            }

            Log(warning is null
                ? $"YANMT: firewall: the active profile(s) {activeNames} are covered by \"{RuleName}\" (TCP {port})."
                : $"YANMT WARNING: firewall: {warning} Active profile(s): {activeNames}. See DEPLOYMENT.md, Troubleshooting.");
            return warning;
        }
        catch (Exception ex)
        {
            // Never fail the install over a read-only check.
            Log("YANMT: could not read the firewall state: " + (ex is TargetInvocationException t && t.InnerException != null ? t.InnerException.Message : ex.Message));
            return null;
        }
        finally
        {
            if (policy != null)
            {
                Marshal.ReleaseComObject(policy);
            }
        }
    }

    private static object? FindRule(object policy)
    {
        var rules = Get(policy, "Rules");
        try
        {
            return rules.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, rules, [RuleName]);
        }
        catch (TargetInvocationException)
        {
            return null;   // INetFwRules.Item throws when no rule has that name.
        }
        finally
        {
            Marshal.ReleaseComObject(rules);
        }
    }

    private static object Get(object target, string property, params object[] args) =>
        target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, args)!;

    private static string Names(int bits)
    {
        var names = new List<string>();
        foreach (var (bit, name) in Profiles)
        {
            if ((bits & bit) != 0)
            {
                names.Add(name);
            }
        }

        return names.Count == 0 ? "none" : string.Join(", ", names);
    }
}
