using Microsoft.Extensions.Logging;
using Yanmt.Infrastructure.Logging;

namespace Yanmt.Infrastructure.Tests.Logging;

public sealed class RollingFileLoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yanmt-tests", "logs-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 3, 1, 23, 59, 0, TimeSpan.Zero));

    [Fact]
    public async Task Writes_formatted_lines_to_a_dated_file()
    {
        using var provider = Create(maxBytes: 1_000_000);
        provider.CreateLogger("Yanmt.Test").LogWarning("ICMP to {Host} failed", "rtr-hub-01");
        await provider.FlushAsync();

        var text = ReadShared(Path.Combine(_dir, "yanmt-20260301.log"));
        Assert.Contains("2026-03-01 23:59:00.000Z WARN Yanmt.Test: ICMP to rtr-hub-01 failed", text);
    }

    [Fact]
    public async Task Rolls_by_size_and_never_writes_back_to_an_older_file()
    {
        using var provider = Create(maxBytes: 400);
        var logger = provider.CreateLogger("T");
        for (var i = 0; i < 20; i++)
        {
            logger.LogInformation("line {I} {Padding}", i, new string('x', 60));
        }

        await provider.FlushAsync();

        var files = LogFiles();
        Assert.True(files.Count > 2, $"expected several files, got {files.Count}");
        Assert.All(files, f => Assert.True(new FileInfo(f).Length <= 400, $"{Path.GetFileName(f)} is over the limit"));

        // Lines appear in order across yanmt-20260301.log, .1.log, .2.log ... with no gaps or reordering.
        var all = string.Concat(files.OrderBy(SequenceOf).Select(ReadShared));
        var positions = Enumerable.Range(0, 20).Select(i => all.IndexOf($"line {i} ", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Order(), positions);
    }

    [Fact]
    public async Task Rolls_to_a_new_file_at_utc_midnight()
    {
        using var provider = Create(maxBytes: 1_000_000);
        var logger = provider.CreateLogger("T");
        logger.LogInformation("before midnight");
        await provider.FlushAsync();
        _clock.Advance(TimeSpan.FromMinutes(2));
        logger.LogInformation("after midnight");
        await provider.FlushAsync();

        Assert.Contains("before midnight", ReadShared(Path.Combine(_dir, "yanmt-20260301.log")));
        Assert.Contains("after midnight", ReadShared(Path.Combine(_dir, "yanmt-20260302.log")));
    }

    [Fact]
    public async Task Keeps_only_the_configured_number_of_files()
    {
        using var provider = Create(maxBytes: 200, retained: 3);
        var logger = provider.CreateLogger("T");
        for (var i = 0; i < 30; i++)
        {
            logger.LogInformation("{Padding}", new string('y', 80));
        }

        await provider.FlushAsync();
        Assert.True(LogFiles().Count <= 3);
    }

    [Fact]
    public async Task Resumes_appending_to_the_newest_file_after_restart()
    {
        using (var first = Create(maxBytes: 1_000_000))
        {
            first.CreateLogger("T").LogInformation("first run");
            await first.FlushAsync();
        }

        using (var second = Create(maxBytes: 1_000_000))
        {
            second.CreateLogger("T").LogInformation("second run");
            await second.FlushAsync();
        }

        var file = Assert.Single(LogFiles());
        var text = ReadShared(file);
        Assert.Contains("first run", text);
        Assert.Contains("second run", text);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private RollingFileLoggerProvider Create(long maxBytes, int retained = 100) =>
        new(new RollingFileLoggerOptions { Directory = _dir, MaxFileSizeBytes = maxBytes, RetainedFileCount = retained }, _clock);

    private List<string> LogFiles() => Directory.GetFiles(_dir, "*.log").ToList();

    // Read the way an operator's tail tool does, while the logger still holds the file open.
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static int SequenceOf(string path)
    {
        var parts = Path.GetFileNameWithoutExtension(path).Split('.');
        return parts.Length == 1 ? 0 : int.Parse(parts[1]);
    }
}
