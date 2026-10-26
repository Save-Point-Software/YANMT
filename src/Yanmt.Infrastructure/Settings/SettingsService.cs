using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yanmt.Core.ConfigBackups;
using Yanmt.Core.Monitoring;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Data.Entities;
using Yanmt.Infrastructure.Monitoring;

namespace Yanmt.Infrastructure.Settings;

/// <summary>A typed handle to one operational setting, with the value used when it was never saved.</summary>
public sealed record SettingKey<T>(string Key, T Default);

public static class SettingKeys
{
    public static readonly SettingKey<StatusColors> StatusColors = new("status.colors", Core.Status.StatusColors.Default);
    public static readonly SettingKey<IcmpSettings> Icmp = new("polling.icmp", new IcmpSettings());
    public static readonly SettingKey<SnmpSettings> Snmp = new("polling.snmp", new SnmpSettings());
    public static readonly SettingKey<ConfigBackupSettings> ConfigBackup = new("backup.config", new ConfigBackupSettings());
}

/// <summary>
/// Reads and writes operational settings (the DB "Setting" table). Writes are audited, and signal
/// the pollers so a changed interval or threshold applies on the next tick.
/// </summary>
public sealed class SettingsService(IDbContextFactory<YanmtDbContext> dbFactory, TimeProvider clock, InventoryChangeSignal? signal = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<T> GetAsync<T>(SettingKey<T> key, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key.Key, cancellationToken);
        if (row is null)
        {
            return key.Default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(row.JsonValue, Json) ?? key.Default;
        }
        catch (JsonException)
        {
            return key.Default; // a hand-edited or outdated value should never take the UI down
        }
    }

    public async Task SetAsync<T>(SettingKey<T> key, T value, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var json = JsonSerializer.Serialize(value, Json);
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key.Key, cancellationToken);
        if (row is null)
        {
            db.Settings.Add(new Setting { Key = key.Key, JsonValue = json, UpdatedUtc = clock.GetUtcNow().UtcDateTime });
        }
        else
        {
            row.JsonValue = json;
            row.UpdatedUtc = clock.GetUtcNow().UtcDateTime;
        }

        await db.SaveChangesAsync(cancellationToken);
        signal?.Notify();
    }
}
