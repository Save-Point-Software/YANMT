using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Yanmt.Infrastructure.Data;

public static class DatabaseInitializer
{
    /// <summary>Enables WAL and applies any pending migrations. Runs once at startup, before the web host starts.</summary>
    public static async Task InitializeAsync(IDbContextFactory<YanmtDbContext> factory, ILogger logger, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        // WAL is a persistent property of the database file; setting it on every start is harmless.
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using (var cmd = db.Database.GetDbConnection().CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode = WAL;";
            var mode = (string?)await cmd.ExecuteScalarAsync(cancellationToken);
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("SQLite did not switch to WAL mode (journal_mode={Mode})", mode);
            }
        }

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count > 0)
        {
            logger.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
            await db.Database.MigrateAsync(cancellationToken);
        }

        await db.Database.CloseConnectionAsync();
    }
}
