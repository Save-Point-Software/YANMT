using Yanmt.Infrastructure.Logging;

namespace Yanmt.Infrastructure.Tests.Logging;

public sealed class StartupLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yanmt-tests", "startup-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Failure_is_appended_with_the_exception_and_timings()
    {
        var at = new DateTimeOffset(2026, 11, 11, 9, 30, 0, TimeSpan.Zero);
        Assert.True(StartupFailureLog.Append(_dir, new InvalidOperationException("Cannot read secrets.entropy"), at, "1.0 s total: x 1.0 s"));
        Assert.True(StartupFailureLog.Append(_dir, new InvalidOperationException("second"), at));

        var text = File.ReadAllText(Path.Combine(_dir, StartupFailureLog.FileName));
        Assert.Contains("2026-11-11 09:30:00Z YANMT failed to start: System.InvalidOperationException: Cannot read secrets.entropy", text);
        Assert.Contains("Startup timings: 1.0 s total: x 1.0 s", text);
        Assert.Contains("second", text); // appended, not overwritten
    }

    [Fact]
    public void Trace_buffers_marks_until_attached_then_writes_each_one_straight_away()
    {
        var start = new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
        var now = start;
        var trace = new StartupTrace(start, () => now);

        now = start.AddSeconds(0.5);
        trace.Mark("runtime start");
        now = start.AddSeconds(29);
        trace.Attach(_dir, @"pid 4242, account YANMT01\svc-yanmt");
        var path = Path.Combine(_dir, StartupTrace.FileName);
        var afterAttach = File.ReadAllText(path);
        Assert.Contains(@"---- process started 2026-11-11 09:00:00.000Z: pid 4242, account YANMT01\svc-yanmt", afterAttach);
        Assert.Contains("+0.5 s runtime start (took 0.5 s)", afterAttach);

        now = start.AddSeconds(30);
        trace.Mark("database and migrations");
        Assert.Contains("+30.0 s database and migrations (took 29.5 s)", File.ReadAllText(path));
        Assert.Equal("30.0 s total: runtime start 0.5 s, database and migrations 29.5 s", trace.Summary());
        Assert.Equal(TimeSpan.FromSeconds(30), trace.Elapsed);
    }

    [Fact]
    public void Trace_never_throws_when_the_folder_is_missing()
    {
        var trace = new StartupTrace(DateTimeOffset.UtcNow);
        trace.Attach(Path.Combine(_dir, "does-not-exist"), "header");
        trace.Mark("still fine");
        Assert.Contains("still fine", trace.Summary());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }
}
