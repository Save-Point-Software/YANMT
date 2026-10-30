using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yanmt.Core.Retention;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Data.Entities;
using Yanmt.Infrastructure.Retention;
using static Yanmt.Infrastructure.Tests.Monitoring.MonitoringTestSupport;

namespace Yanmt.Infrastructure.Tests.Rollups;

public class RetentionServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RetentionService Create(TestDatabase test, RetentionSettings settings) =>
        new(test.Factory, _ => Task.FromResult(settings), test.Clock, NullLogger<RetentionService>.Instance);

    [Fact]
    public async Task Purges_rolled_up_raw_samples_old_rollups_and_old_events()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-a", "192.0.2.1");
        var now = test.Clock.GetUtcNow().UtcDateTime; // 2026-01-15 12:00
        await using (var db = test.Open())
        {
            db.IcmpSamples.AddRange(
                new IcmpSample { NodeId = node, TimestampUtc = now.AddDays(-10), Sent = 4, Received = 4 },
                new IcmpSample { NodeId = node, TimestampUtc = now.AddDays(-1), Sent = 4, Received = 4 });
            db.IcmpRollupHours.AddRange(
                new IcmpRollupHour { NodeId = node, HourUtc = new DateTime(2025, 9, 1, 0, 0, 0, DateTimeKind.Utc), Sent = 4, Received = 4, SampleCount = 1 },
                new IcmpRollupHour { NodeId = node, HourUtc = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc), Sent = 4, Received = 4, SampleCount = 1 });
            db.Events.AddRange(
                new Event { TimestampUtc = now.AddDays(-400), NodeId = node, NewState = Severity.Critical, Message = "old" },
                new Event { TimestampUtc = now.AddDays(-5), NodeId = node, NewState = Severity.Critical, Message = "recent" });
            await db.SaveChangesAsync(Ct);
        }

        var result = await Create(test, new RetentionSettings()).RunAsync(ct: Ct);

        Assert.Equal(1, result.RawSamples);
        Assert.Equal(1, result.Rollups);
        Assert.Equal(1, result.Events);
        await using var read = test.Open();
        Assert.Equal(1, await read.IcmpSamples.CountAsync(Ct));
        Assert.Equal(1, await read.IcmpRollupHours.CountAsync(Ct));
        Assert.Equal("recent", (await read.Events.SingleAsync(Ct)).Message);
    }

    [Fact]
    public async Task Does_not_purge_raw_samples_that_have_not_been_rolled_up()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-a", "192.0.2.1");
        var now = test.Clock.GetUtcNow().UtcDateTime;
        await using (var db = test.Open())
        {
            db.IcmpSamples.Add(new IcmpSample { NodeId = node, TimestampUtc = now.AddDays(-30), Sent = 4, Received = 4 });
            await db.SaveChangesAsync(Ct);
        }

        var result = await Create(test, new RetentionSettings()).RunAsync(ct: Ct);

        Assert.Equal(0, result.RawSamples);
        await using var read = test.Open();
        Assert.Equal(1, await read.IcmpSamples.CountAsync(Ct));
    }

    [Fact]
    public async Task Config_versions_are_kept_by_default_and_trimmed_to_newest_n_per_node()
    {
        await using var test = await TestDatabase.CreateAsync();
        var a = await test.AddNodeAsync("lab-rtr-a", "192.0.2.1");
        var b = await test.AddNodeAsync("lab-rtr-b", "192.0.2.2");
        var now = test.Clock.GetUtcNow().UtcDateTime;
        await using (var db = test.Open())
        {
            for (var i = 0; i < 5; i++)
            {
                db.ConfigVersions.Add(new ConfigVersion { NodeId = a, CapturedUtc = now.AddHours(-i), ContentHash = $"a{i}", ProtectedRaw = [1] });
            }

            db.ConfigVersions.Add(new ConfigVersion { NodeId = b, CapturedUtc = now.AddDays(-200), ContentHash = "b0", ProtectedRaw = [1] });
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(0, (await Create(test, new RetentionSettings()).RunAsync(ct: Ct)).ConfigVersions);

        var result = await Create(test, new RetentionSettings { KeepConfigVersions = 2 }).RunAsync(ct: Ct);

        Assert.Equal(3, result.ConfigVersions);
        await using var read = test.Open();
        var kept = await read.ConfigVersions.Select(v => v.ContentHash).OrderBy(h => h).ToListAsync(Ct);
        Assert.Equal(["a0", "a1", "b0"], kept); // newest per node survives, even when old
    }

    [Fact]
    public async Task Each_run_writes_one_audit_entry_with_counts_only_and_skips_vacuum_when_little_is_free()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-a", "192.0.2.1");
        var now = test.Clock.GetUtcNow().UtcDateTime;
        await using (var db = test.Open())
        {
            db.Events.Add(new Event { TimestampUtc = now.AddDays(-400), NodeId = node, NewState = Severity.Critical, Message = "secret-looking row text" });
            await db.SaveChangesAsync(Ct);
        }

        var service = Create(test, new RetentionSettings());
        var result = await service.RunAsync(@"LAB\admin", Ct);
        await service.RunAsync(ct: Ct);

        Assert.False(result.Vacuumed);
        await using var read = test.Open();
        var entries = await read.AuditEntries.Where(a => a.Action == "RetentionCleanup").OrderBy(a => a.Id).ToListAsync(Ct);
        Assert.Equal(2, entries.Count);
        Assert.Equal(@"LAB\admin", entries[0].User);
        Assert.Equal("system", entries[1].User);
        Assert.Contains("\"events\":1", entries[0].Changes);
        Assert.Contains("\"events\":0", entries[1].Changes);
        Assert.DoesNotContain("secret-looking", entries[0].Changes);
    }

    [Fact]
    public void Next_run_is_the_coming_0300_utc()
    {
        Assert.Equal(TimeSpan.FromHours(15), RetentionWorker.UntilNextRun(new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(TimeSpan.FromHours(1), RetentionWorker.UntilNextRun(new DateTime(2026, 1, 15, 2, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(TimeSpan.FromHours(24), RetentionWorker.UntilNextRun(new DateTime(2026, 1, 15, 3, 0, 0, DateTimeKind.Utc)));
    }
}
