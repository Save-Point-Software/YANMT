using Microsoft.EntityFrameworkCore;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Data.Entities;
using Yanmt.Infrastructure.Settings;

namespace Yanmt.Infrastructure.Tests.Settings;

public class SettingsServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unsaved_setting_returns_default()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = new SettingsService(test.Factory, test.Clock);

        Assert.Equal(StatusColors.Default, await svc.GetAsync(SettingKeys.StatusColors, Ct));
    }

    [Fact]
    public async Task Saved_setting_round_trips_and_is_audited()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = new SettingsService(test.Factory, test.Clock);
        var custom = StatusColors.Default with { Warning = "#FF9900" };

        await svc.SetAsync(SettingKeys.StatusColors, custom, Ct);
        await svc.SetAsync(SettingKeys.StatusColors, custom with { Critical = "#AA0000" }, Ct);

        Assert.Equal(custom with { Critical = "#AA0000" }, await svc.GetAsync(SettingKeys.StatusColors, Ct));
        await using var db = test.Open();
        Assert.Equal(["Create", "Update"], await db.AuditEntries.Where(a => a.EntityType == nameof(Setting)).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync(Ct));
    }

    [Fact]
    public async Task Corrupt_value_falls_back_to_default()
    {
        await using var test = await TestDatabase.CreateAsync();
        await using (var db = test.Open())
        {
            db.Settings.Add(new Setting { Key = SettingKeys.StatusColors.Key, JsonValue = "{not json" });
            await db.SaveChangesAsync(Ct);
        }

        var svc = new SettingsService(test.Factory, test.Clock);
        Assert.Equal(StatusColors.Default, await svc.GetAsync(SettingKeys.StatusColors, Ct));
    }
}
