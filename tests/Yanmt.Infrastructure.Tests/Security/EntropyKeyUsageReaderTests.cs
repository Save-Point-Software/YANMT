using Yanmt.Infrastructure.Data.Entities;
using Yanmt.Infrastructure.Security;
using Yanmt.Infrastructure.Tests.Monitoring;

namespace Yanmt.Infrastructure.Tests.Security;

public sealed class EntropyKeyUsageReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Empty_database_does_not_use_the_key()
    {
        await using var test = await TestDatabase.CreateAsync();
        await using var db = test.Open();

        var usage = await EntropyKeyUsageReader.ReadAsync(db, Ct);

        Assert.False(usage.InUse);
    }

    [Fact]
    public async Task Config_history_counts_even_after_the_last_credential_profile_is_gone()
    {
        await using var test = await TestDatabase.CreateAsync();
        var node = await test.AddNodeAsync("lab-rtr-a", "192.0.2.1");
        await using (var db = test.Open())
        {
            for (var i = 0; i < 3; i++)
            {
                db.ConfigVersions.Add(new ConfigVersion
                {
                    NodeId = node, CapturedUtc = test.Clock.GetUtcNow().UtcDateTime.AddHours(-i), ContentHash = $"h{i}", ProtectedRaw = [1],
                });
            }

            await db.SaveChangesAsync(Ct);
        }

        await using var read = test.Open();
        var usage = await EntropyKeyUsageReader.ReadAsync(read, Ct);

        Assert.Equal(0, usage.CredentialProfiles);
        Assert.Equal(3, usage.ConfigVersions);
        Assert.True(usage.InUse);
    }
}
