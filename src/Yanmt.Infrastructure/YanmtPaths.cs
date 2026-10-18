namespace Yanmt.Infrastructure;

/// <summary>Bootstrap options bound from the "Yanmt" section of appsettings.json.</summary>
public sealed class YanmtOptions
{
    public const string SectionName = "Yanmt";

    /// <summary>
    /// Data folder (DB, config backups, map images, logs). Empty means %ProgramData%\YANMT.
    /// A relative path is resolved against the application's content root.
    /// </summary>
    public string? DataPath { get; set; }
}

/// <summary>Resolved locations inside the data folder.</summary>
public sealed class YanmtPaths
{
    public YanmtPaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }
    public string Database => Path.Combine(Root, "yanmt.db");
    public string Logs => Path.Combine(Root, "logs");
    public string Backups => Path.Combine(Root, "backups");
    public string MapImages => Path.Combine(Root, "maps");

    /// <summary>Extra DPAPI entropy for stored credentials. Back it up with the database.</summary>
    public string EntropyFile => Path.Combine(Root, "secrets.entropy");

    public static YanmtPaths Resolve(string? configuredPath, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return new YanmtPaths(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "YANMT"));
        }

        var expanded = Environment.ExpandEnvironmentVariables(configuredPath);
        return new YanmtPaths(Path.IsPathRooted(expanded) ? expanded : Path.Combine(contentRoot, expanded));
    }

    /// <summary>Creates the folder tree. Throws with the path in the message if the service account lacks rights.</summary>
    public void EnsureCreated()
    {
        foreach (var dir in new[] { Root, Logs, Backups, MapImages })
        {
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException(
                    $"Cannot create or access '{dir}'. Grant the YANMT service account Modify rights on the data folder.", ex);
            }
        }
    }
}
