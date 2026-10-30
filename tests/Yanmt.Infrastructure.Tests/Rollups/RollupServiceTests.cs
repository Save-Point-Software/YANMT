using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yanmt.Infrastructure.Data.Entities;
using Yanmt.Infrastructure.Rollups;
using static Yanmt.Infrastructure.Tests.Monitoring.MonitoringTestSupport;

namespace Yanmt.Infrastructure.Tests.Rollups;

public class RollupServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Rolls_up_closed_hours_only_and_is_idempotent()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-a", "192.0.2.1");
        var h10 = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        await using (var db = test.Open())
        {
            db.IcmpSamples.AddRange(
                new IcmpSample { NodeId = node, TimestampUtc = h10.AddMinutes(1), Sent = 4, Received = 4, MinMs = 1, AvgMs = 2, MaxMs = 5, LossPercent = 0 },
                new IcmpSample { NodeId = node, TimestampUtc = h10.AddMinutes(30), Sent = 4, Received = 2, MinMs = 3, AvgMs = 6, MaxMs = 9, LossPercent = 50 },
                // Current (open) hour: must not be rolled up.
                new IcmpSample { NodeId = node, TimestampUtc = h10.AddHours(2).AddMinutes(5), Sent = 4, Received = 4, MinMs = 1, AvgMs = 1, MaxMs = 1, LossPercent = 0 });
            await db.SaveChangesAsync(Ct);
        }

        var service = new RollupService(test.Factory, test.Clock, NullLogger<RollupService>.Instance);
        await service.RollUpClosedHoursAsync(Ct);
        await service.RollUpClosedHoursAsync(Ct);

        await using var read = test.Open();
        var rows = await read.IcmpRollupHours.ToListAsync(Ct);
        var row = Assert.Single(rows);
        Assert.Equal(h10, row.HourUtc);
        Assert.Equal(8, row.Sent);
        Assert.Equal(6, row.Received);
        Assert.Equal(25, row.LossPercent, 6);
        Assert.Equal(9, row.MaxMs);
    }

    [Fact]
    public async Task Catches_up_after_a_gap_longer_than_one_run()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-b", "192.0.2.2");
        var first = new DateTime(2026, 1, 8, 10, 0, 0, DateTimeKind.Utc);
        var afterGap = first.AddHours(72 + 5); // 77 empty hours in between
        await using (var db = test.Open())
        {
            foreach (var h in new[] { first, afterGap })
            {
                db.IcmpSamples.Add(new IcmpSample { NodeId = node, TimestampUtc = h.AddMinutes(3), Sent = 4, Received = 4, MinMs = 1, AvgMs = 2, MaxMs = 3, LossPercent = 0 });
            }
            await db.SaveChangesAsync(Ct);
        }

        var service = new RollupService(test.Factory, test.Clock, NullLogger<RollupService>.Instance);
        // Same loop as RollupWorker, bounded so a regression fails instead of hanging.
        var runs = 0;
        while (await service.RollUpClosedHoursAsync(Ct) >= RollupService.MaxHoursPerRun)
        {
            Assert.True(++runs < 10, "rollup did not catch up");
        }

        await using var read = test.Open();
        var hours = await read.IcmpRollupHours.OrderBy(r => r.HourUtc).Select(r => r.HourUtc).ToListAsync(Ct);
        Assert.Equal(new[] { first, afterGap }, hours);
    }
}
