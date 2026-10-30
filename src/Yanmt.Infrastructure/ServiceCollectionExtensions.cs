using Yanmt.Infrastructure.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Yanmt.Core.Oids;
using Yanmt.Infrastructure.Collectors.Icmp;
using Yanmt.Infrastructure.Collectors.Snmp;
using Yanmt.Infrastructure.Collectors.Ssh;
using Yanmt.Infrastructure.ConfigBackups;
using Yanmt.Infrastructure.Connectivity;
using Yanmt.Infrastructure.Credentials;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Inventory;
using Yanmt.Infrastructure.Maps;
using Yanmt.Infrastructure.Monitoring;
using Yanmt.Infrastructure.Monitors;
using Yanmt.Infrastructure.Security;
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

        services.AddSingleton<SecretProtector>();
        services.AddScoped<CredentialService>();
        services.AddSingleton<IIcmpProbe, IcmpProbe>();
        services.AddSingleton<ISnmpReader, SnmpV3Reader>();
        services.AddSingleton<ReadOnlySshCollector>();
        services.AddScoped<ConnectionTester>();
        services.AddScoped<ConfigBackupService>();
        services.AddScoped<ConfigHistoryService>();

        services.AddSingleton<InventoryChangeSignal>();
        services.AddSingleton<StatusCache>();
        services.AddSingleton<StatusBroadcaster>();
        services.AddSingleton<InAppNotifier>();
        services.AddSingleton<INotifier>(sp => sp.GetRequiredService<InAppNotifier>());
        services.AddScoped<EventService>();
        services.AddScoped<MapService>();
        services.AddSingleton<IOidNameResolver>(BuiltInOidNames.Instance);
        services.AddScoped<SnmpTargetResolver>();
        services.AddScoped<InterfaceService>();
        services.AddScoped<SnmpQueryService>();
        services.AddScoped<NodeDetailService>();
        services.AddScoped<MonitorService>();
        services.AddScoped<MonitorProbeService>();
        services.AddSingleton<MonitorCollector>();
        return services;
    }

    /// <summary>
    /// The polling engine: the status engine (a singleton with its own long-lived scope, so it
    /// doesn't depend on any web request) and the hosted services that run it and the ICMP poller.
    /// </summary>
    public static IServiceCollection AddYanmtMonitoring(this IServiceCollection services)
    {
        services.AddOptions<PollingOptions>();
        services.AddSingleton(sp => new StatusEngine(
            sp.CreateScope().ServiceProvider.GetRequiredService<IDbContextFactory<YanmtDbContext>>(),
            sp.GetRequiredService<StatusCache>(),
            sp.GetRequiredService<StatusBroadcaster>(),
            sp.GetRequiredService<INotifier>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<StatusEngine>>()));
        services.AddHostedService<StatusEngineService>();
        services.AddSingleton<DefaultStatsCollector>();
        services.AddHostedService<IcmpPoller>();
        services.AddHostedService<SnmpPoller>();
        services.AddHostedService<MonitorPoller>();
        services.AddHostedService<ConfigBackupPoller>();
        // The context factory is scoped; like StatusEngine, hold one factory from a long-lived scope.
        services.AddSingleton(sp => new Yanmt.Infrastructure.Rollups.RollupService(
            sp.CreateScope().ServiceProvider.GetRequiredService<IDbContextFactory<YanmtDbContext>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Yanmt.Infrastructure.Rollups.RollupService>>()));
        services.AddHostedService<Yanmt.Infrastructure.Rollups.RollupWorker>();
        services.AddSingleton(sp =>
        {
            var scope = sp.CreateScope().ServiceProvider;
            var settings = scope.GetRequiredService<SettingsService>();
            return new Yanmt.Infrastructure.Retention.RetentionService(
                scope.GetRequiredService<IDbContextFactory<YanmtDbContext>>(),
                ct => settings.GetAsync(SettingKeys.Retention, ct),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Yanmt.Infrastructure.Retention.RetentionService>>());
        });
        services.AddHostedService<Yanmt.Infrastructure.Retention.RetentionWorker>();
        return services;
    }
}
