using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using Yanmt.Core.Security;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Logging;
using Yanmt.Infrastructure.Monitors;
using Yanmt.Infrastructure.Security;

namespace Yanmt.Web.Hosting;

/// <summary>
/// The slow part of startup: role groups, migrations, the starter library, and the credential key.
/// Registered as the first hosted service, so it runs after the service manager has been told the
/// service is running (migrations never count against its 30 s start timeout), and before the pollers
/// and the web server start. Any exception here fails host start, and Program exits with code 1.
/// </summary>
public sealed class StartupInitializer(
    IServiceProvider services,
    StartupTrace trace,
    ILoggerFactory loggerFactory) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Yanmt.Startup");
        trace.Mark(WindowsServiceHelpers.IsWindowsService() ? "host start (service manager told: running)" : "host start");

        // Resolve role groups now so misconfiguration is logged at startup, not on the first request.
        _ = services.GetRequiredService<RoleGroupMap>();
        trace.Mark("role groups");

        using var scope = services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<YanmtDbContext>>();
        await DatabaseInitializer.InitializeAsync(factory, logger, cancellationToken);
        trace.Mark("database and migrations");
        await StarterLibrary.SeedAsync(factory, services.GetRequiredService<TimeProvider>());
        trace.Mark("starter library");

        // Load (or create) the credential entropy file now, so an ACL problem stops startup clearly.
        var protector = services.GetRequiredService<SecretProtector>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        try
        {
            protector.EnsureReady();
        }
        catch (EntropyFileUnreadableException ex)
        {
            // The database is up, so say whether deleting the file would lose anything.
            var saved = await db.CredentialProfiles.CountAsync(cancellationToken);
            throw new InvalidOperationException(EntropyFileRules.UnreadableMessage(ex.Path, ex.Account, saved), ex);
        }

        if (protector.CreatedNewKey && await db.CredentialProfiles.AnyAsync(cancellationToken))
        {
            logger.LogError(
                "A new credential key file was created, but credential profiles already exist. Their passwords can't be decrypted: " +
                "restore secrets.entropy from backup, or re-enter the passwords under Credentials.");
        }

        trace.Mark("credential key");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
