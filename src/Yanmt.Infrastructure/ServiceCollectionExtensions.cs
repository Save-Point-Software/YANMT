using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Inventory;
using Yanmt.Infrastructure.Settings;

namespace Yanmt.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the database and application services. The DbContext factory is scoped, so each
    /// Blazor circuit (or background-service scope) gets an audit interceptor bound to its own
    /// <see cref="ICurrentUser"/>. Hosts register their own ICurrentUser; the fallback is "system".
    /// </summary>
    public static IServiceCollection AddYanmtInfrastructure(this IServiceCollection services, YanmtPaths paths)
    {
        services.AddSingleton(paths);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ICurrentUser, SystemUser>();
        services.AddSingleton<SqlitePragmaInterceptor>();
        services.AddScoped<AuditInterceptor>();

        services.AddDbContextFactory<YanmtDbContext>(
            (sp, options) => options
                .UseSqlite($"Data Source={paths.Database}")
                .AddInterceptors(sp.GetRequiredService<SqlitePragmaInterceptor>(), sp.GetRequiredService<AuditInterceptor>()),
            ServiceLifetime.Scoped);

        services.AddScoped<NodeService>();
        services.AddScoped<SettingsService>();
        services.AddScoped<AuditLogService>();
        return services;
    }
}
