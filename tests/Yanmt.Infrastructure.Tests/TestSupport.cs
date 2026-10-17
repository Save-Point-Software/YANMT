using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Yanmt.Infrastructure.Data;

namespace Yanmt.Infrastructure.Tests;

/// <summary>A migrated SQLite database in a temp file (a real file, so WAL behaves as in production).</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _dir;

    private TestDatabase(string dir, TestUser user, ManualClock clock)
    {
        _dir = dir;
        User = user;
        Clock = clock;
        Path = System.IO.Path.Combine(dir, "yanmt-test.db");
        var options = new DbContextOptionsBuilder<YanmtDbContext>()
            .UseSqlite($"Data Source={Path};Pooling=False")
            .AddInterceptors(new SqlitePragmaInterceptor(), new AuditInterceptor(user, clock))
            .Options;
        Factory = new OptionsFactory(options);
    }

    public string Path { get; }
    public TestUser User { get; }
    public ManualClock Clock { get; }
    public IDbContextFactory<YanmtDbContext> Factory { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yanmt-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db = new TestDatabase(dir, new TestUser(@"LAB\alice"), new ManualClock(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)));
        await DatabaseInitializer.InitializeAsync(db.Factory, NullLogger.Instance);
        return db;
    }

    public YanmtDbContext Open() => Factory.CreateDbContext();

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; temp is cleaned eventually.
        }

        return ValueTask.CompletedTask;
    }

    private sealed class OptionsFactory(DbContextOptions<YanmtDbContext> options) : IDbContextFactory<YanmtDbContext>
    {
        public YanmtDbContext CreateDbContext() => new(options);
    }
}

public sealed class TestUser(string name) : ICurrentUser
{
    public string Name { get; set; } = name;

    public ValueTask<string> GetNameAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Name);
}

public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}
