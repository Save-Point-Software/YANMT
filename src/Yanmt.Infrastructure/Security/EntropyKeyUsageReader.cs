using Microsoft.EntityFrameworkCore;
using Yanmt.Core.Security;
using Yanmt.Infrastructure.Data;

namespace Yanmt.Infrastructure.Security;

/// <summary>
/// Counts the rows encrypted with <see cref="SecretProtector"/>: credential profiles and config
/// backup versions. Config history outlives the credentials that captured it, so zero profiles
/// alone never means the key is unused. Add any new protected table here.
/// </summary>
public static class EntropyKeyUsageReader
{
    public static async Task<EntropyKeyUsage> ReadAsync(YanmtDbContext db, CancellationToken cancellationToken = default) =>
        new(await db.CredentialProfiles.CountAsync(cancellationToken), await db.ConfigVersions.CountAsync(cancellationToken));
}
