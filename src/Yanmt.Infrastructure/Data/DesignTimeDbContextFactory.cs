using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Yanmt.Infrastructure.Data;

/// <summary>Lets `dotnet ef migrations add` run against this project without starting the web host.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<YanmtDbContext>
{
    public YanmtDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<YanmtDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
