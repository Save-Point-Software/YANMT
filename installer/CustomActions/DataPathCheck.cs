using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Yanmt.Installer.CustomActions;

/// <summary>
/// The MSI only supports the default data folder, %ProgramData%\YANMT: its ACLs, the pre-upgrade backup
/// and the startup-log hints all use it. A custom Yanmt:DataPath (a supported manual-install setting) is
/// refused before anything changes, so a manual install's service keeps running untouched.
/// Mirrors YanmtPaths.Resolve in the app: environment variables expanded, a relative path is relative to
/// the install folder (the service's content root).
/// </summary>
internal static class DataPathCheck
{
    private static readonly Regex DataPathEntry = new(
        "\"DataPath\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Null when every source leaves the default; otherwise the reason to refuse.</summary>
    public static string? FindCustomDataPath(string installFolder)
    {
        var defaultPath = Normalize(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "YANMT"));
        foreach (var (source, value) in Sources(installFolder))
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var expanded = Environment.ExpandEnvironmentVariables(value!.Trim());
            string resolved;
            try
            {
                resolved = Normalize(Path.IsPathRooted(expanded) ? expanded : Path.Combine(installFolder, expanded));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                resolved = expanded;
            }

            if (!string.Equals(resolved, defaultPath, StringComparison.OrdinalIgnoreCase))
            {
                return $"{source} sets Yanmt:DataPath to '{value}' ({resolved}). The installer supports only the default " +
                    $"data folder {defaultPath}: it sets that folder's permissions and backs it up before upgrades. " +
                    "Nothing was changed. Move the data to the default folder and remove the setting, or keep the manual install.";
            }
        }

        return null;
    }

    private static IEnumerable<(string Source, string? Value)> Sources(string installFolder)
    {
        // The service's own command line (a manual install could pass --Yanmt:DataPath=...).
        using (var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\YANMT"))
        {
            var imagePath = service?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (imagePath != null && imagePath.IndexOf("DataPath", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                yield return ("The YANMT service command line", imagePath);
            }

            // Per-service environment (REG_MULTI_SZ "NAME=value").
            if (service?.GetValue("Environment") is string[] environment)
            {
                foreach (var entry in environment)
                {
                    var eq = entry.IndexOf('=');
                    if (eq > 0 && IsDataPathVariable(entry.Substring(0, eq)))
                    {
                        yield return ("The YANMT service's environment", entry.Substring(eq + 1));
                    }
                }
            }
        }

        foreach (var name in new[] { "Yanmt__DataPath", "Yanmt:DataPath" })
        {
            yield return ($"The machine environment variable {name}",
                Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine));
        }

        var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT", EnvironmentVariableTarget.Machine)
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", EnvironmentVariableTarget.Machine)
            ?? "Production";
        foreach (var file in new[] { "appsettings.json", $"appsettings.{environmentName}.json" })
        {
            var path = Path.Combine(installFolder, file);
            if (!File.Exists(path))
            {
                continue;
            }

            // A text search, not a JSON parse: appsettings.json may have comments and trailing commas,
            // and .NET Framework has no parser for that. "DataPath" is only used under "Yanmt".
            foreach (Match match in DataPathEntry.Matches(File.ReadAllText(path)))
            {
                yield return (path, Unescape(match.Groups[1].Value));
            }
        }
    }

    private static bool IsDataPathVariable(string name) =>
        string.Equals(name, "Yanmt__DataPath", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Yanmt:DataPath", StringComparison.OrdinalIgnoreCase);

    private static string Unescape(string json)
    {
        try
        {
            return Regex.Unescape(json.Replace("\\/", "/"));
        }
        catch (ArgumentException)
        {
            return json;
        }
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\');
}
