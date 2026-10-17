using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Yanmt.Infrastructure.Data;

/// <summary>
/// Per-connection SQLite settings. journal_mode=WAL is persistent and set once at startup by
/// <see cref="DatabaseInitializer"/>; these must be applied on every connection.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Pragmas = "PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
