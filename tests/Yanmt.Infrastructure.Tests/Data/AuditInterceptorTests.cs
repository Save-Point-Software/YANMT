using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yanmt.Core.Inventory;
using Yanmt.Infrastructure.Data.Entities;

namespace Yanmt.Infrastructure.Tests.Data;

public class AuditInterceptorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_is_audited_with_user_key_and_values()
    {
        await using var test = await TestDatabase.CreateAsync();
        var id = await AddNodeAsync(test);

        await using var db = test.Open();
        var entry = await db.AuditEntries.SingleAsync(Ct);
        Assert.Equal(@"LAB\alice", entry.User);
        Assert.Equal("Create", entry.Action);
        Assert.Equal(nameof(Node), entry.EntityType);
        Assert.Equal(id.ToString(), entry.EntityKey);
        Assert.Equal(test.Clock.Now.UtcDateTime, entry.TimestampUtc);

        var values = JsonDocument.Parse(entry.Changes!).RootElement;
        Assert.Equal("rtr-hub-01", values.GetProperty("Hostname").GetString());
        Assert.Equal("Router", values.GetProperty("Icon").GetString());
    }

    [Fact]
    public async Task Update_records_old_and_new_values_of_changed_fields_only()
    {
        await using var test = await TestDatabase.CreateAsync();
        var id = await AddNodeAsync(test);
        test.User.Name = @"LAB\bob";

        await using (var db = test.Open())
        {
            var node = await db.Nodes.SingleAsync(n => n.Id == id, Ct);
            node.ManagementIp = "192.0.2.99";
            node.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = test.Open())
        {
            var update = await db.AuditEntries.SingleAsync(a => a.Action == "Update", Ct);
            Assert.Equal(@"LAB\bob", update.User);
            var changes = JsonDocument.Parse(update.Changes!).RootElement;
            Assert.Equal("192.0.2.1", changes.GetProperty("ManagementIp").GetProperty("old").GetString());
            Assert.Equal("192.0.2.99", changes.GetProperty("ManagementIp").GetProperty("new").GetString());
            Assert.False(changes.TryGetProperty("UpdatedUtc", out _));
            Assert.False(changes.TryGetProperty("Hostname", out _));
        }
    }

    [Fact]
    public async Task Timestamp_only_update_is_not_audited()
    {
        await using var test = await TestDatabase.CreateAsync();
        var id = await AddNodeAsync(test);

        await using (var db = test.Open())
        {
            var node = await db.Nodes.SingleAsync(n => n.Id == id, Ct);
            node.UpdatedUtc = node.UpdatedUtc.AddMinutes(5);
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = test.Open())
        {
            Assert.Equal(1, await db.AuditEntries.CountAsync(Ct));
        }
    }

    [Fact]
    public async Task Delete_is_audited_with_last_values()
    {
        await using var test = await TestDatabase.CreateAsync();
        var id = await AddNodeAsync(test);

        await using (var db = test.Open())
        {
            db.Nodes.Remove(await db.Nodes.SingleAsync(n => n.Id == id, Ct));
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = test.Open())
        {
            var delete = await db.AuditEntries.SingleAsync(a => a.Action == "Delete", Ct);
            Assert.Equal(id.ToString(), delete.EntityKey);
            Assert.Contains("rtr-hub-01", delete.Changes);
        }
    }

    [Fact]
    public async Task Synchronous_save_of_audited_entity_is_refused()
    {
        await using var test = await TestDatabase.CreateAsync();
        await using var db = test.Open();
        db.Nodes.Add(NewNode());

        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
    }

    [Fact]
    public async Task Unaudited_entities_produce_no_audit_rows()
    {
        await using var test = await TestDatabase.CreateAsync();
        await using var db = test.Open();
        db.Tags.Add(new Tag { Name = "core" });
        await db.SaveChangesAsync(Ct);

        Assert.Equal(0, await db.AuditEntries.CountAsync(Ct));
    }

    private static Node NewNode() => new()
    {
        Hostname = "rtr-hub-01",
        ManagementIp = "192.0.2.1",
        Icon = NodeIconType.Router,
    };

    private static async Task<int> AddNodeAsync(TestDatabase test)
    {
        await using var db = test.Open();
        var node = NewNode();
        db.Nodes.Add(node);
        await db.SaveChangesAsync(Ct);
        return node.Id;
    }
}
