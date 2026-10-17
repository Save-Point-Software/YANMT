using System.Text.Json;

namespace Yanmt.Integration.Tests;

/// <summary>
/// The CML lab the opt-in integration tests target. Loaded from tests/lab.json (gitignored), or
/// from the path in the YANMT_LAB_CONFIG environment variable. Copy tests/lab.example.json to start.
/// Credential sections are added in Phase 1b.
/// </summary>
public sealed class LabConfig
{
    public List<LabNode> Nodes { get; set; } = [];

    private static readonly Lazy<LabConfig?> Cached = new(Load);

    /// <summary>The lab config, or skips the calling test when there is none.</summary>
    public static LabConfig Require()
    {
        Assert.SkipWhen(Cached.Value is null, "No lab config: copy tests/lab.example.json to tests/lab.json (or set YANMT_LAB_CONFIG).");
        return Cached.Value!;
    }

    private static LabConfig? Load()
    {
        var path = Environment.GetEnvironmentVariable("YANMT_LAB_CONFIG") is { Length: > 0 } env ? env : FindDefault();
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<LabConfig>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
               ?? throw new InvalidOperationException($"{path} is empty or not valid JSON.");
    }

    private static string? FindDefault()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Yanmt.slnx")))
            {
                return Path.Combine(dir.FullName, "tests", "lab.json");
            }
        }

        return null;
    }
}

public sealed class LabNode
{
    public string Hostname { get; set; } = "";
    public string ManagementIp { get; set; } = "";
}
