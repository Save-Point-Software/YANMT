using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Win32;
using Yanmt.Core.Installer;

namespace Yanmt.Web.Hosting;

/// <summary>
/// Adds the settings the MSI wrote to HKLM\SOFTWARE\YANMT as a configuration layer above the JSON
/// files and below environment variables and the command line. Read once at startup, like the rest
/// of the HTTPS and role settings (a change needs a service restart). No key means a manual install:
/// nothing is added.
/// </summary>
public static class InstallerConfiguration
{
    /// <summary>Returns the configuration keys that were set (for the startup log).</summary>
    public static IReadOnlyList<string> Add(ConfigurationManager configuration)
    {
        var values = ReadRegistry();
        if (values.Count == 0)
        {
            return [];
        }

        var mapped = InstallerSettings.ToConfiguration(
            values,
            configuration.GetSection(InstallerSettings.AdminGroupsKey).GetChildren().Count(),
            configuration.GetSection(InstallerSettings.ViewerGroupsKey).GetChildren().Count());
        if (mapped.Count == 0)
        {
            return [];
        }

        // Right after the last JSON file (appsettings.json, appsettings.{Environment}.json, user secrets).
        var sources = configuration.Sources;
        var index = 0;
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is JsonConfigurationSource)
            {
                index = i + 1;
            }
        }

        sources.Insert(index, new MemoryConfigurationSource { InitialData = mapped });
        return [.. mapped.Keys.Select(WithoutIndex).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>"Yanmt:Roles:AdminGroups:1" -> "Yanmt:Roles:AdminGroups".</summary>
    private static string WithoutIndex(string key)
    {
        var colon = key.LastIndexOf(':');
        return colon > 0 && key[(colon + 1)..].All(char.IsAsciiDigit) ? key[..colon] : key;
    }

    private static Dictionary<string, object?> ReadRegistry()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(InstallerSettings.RegistryKeyPath);
        if (key is null)
        {
            return values;
        }

        foreach (var name in new[]
        {
            InstallerSettings.PortValue, InstallerSettings.CertificateThumbprintValue,
            InstallerSettings.AdminGroupsValue, InstallerSettings.ViewerGroupsValue,
        })
        {
            // Environment variables in a REG_EXPAND_SZ are not expanded: none of these should hold any.
            if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } value)
            {
                values[name] = value;
            }
        }

        return values;
    }
}
