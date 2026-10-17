using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yanmt.Infrastructure.Data;

namespace Yanmt.Infrastructure.Tests.Data;

public class DatabaseTests
{
    [Fact]
    public async Task Initializer_applies_migrations_and_enables_wal()
    {
        await using var test = await TestDatabase.CreateAsync();
        await using var db = test.Open();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.Equal("wal", await ScalarAsync(db, "PRAGMA journal_mode;"));
    }

    [Fact]
    public async Task Per_connection_pragmas_are_applied()
    {
        await using var test = await TestDatabase.CreateAsync();
        await using var db = test.Open();

        Assert.Equal("1", await ScalarAsync(db, "PRAGMA synchronous;")); // NORMAL
        Assert.Equal("1", await ScalarAsync(db, "PRAGMA foreign_keys;"));
        Assert.Equal("5000", await ScalarAsync(db, "PRAGMA busy_timeout;"));
    }

    [Fact]
    public async Task Model_has_no_changes_missing_from_migrations()
    {
        await using var test = await TestDatabase.CreateAsync();
        await using var db = test.Open();

        // Fails when an entity changes without `dotnet ef migrations add`.
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Initializer_is_idempotent()
    {
        await using var test = await TestDatabase.CreateAsync();
        await DatabaseInitializer.InitializeAsync(test.Factory, NullLogger.Instance, TestContext.Current.CancellationToken);
    }

    private static async Task<string?> ScalarAsync(YanmtDbContext db, string sql)
    {
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        return (await cmd.ExecuteScalarAsync())?.ToString();
    }
}
