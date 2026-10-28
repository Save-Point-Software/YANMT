using Microsoft.EntityFrameworkCore;
using Yanmt.Infrastructure.ConfigBackups;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Security;
using System.Text;

namespace Yanmt.Web.Endpoints;

public static class ConfigDownloadEndpoint
{
    /// <summary>
    /// Viewer access (the fallback policy). Both downloads are masked unless an admin passes
    /// <c>?secrets=true</c>; <see cref="ConfigHistoryService"/> enforces that and audits it, so a viewer
    /// adding the flag just gets the masked file.
    /// </summary>
    public static void MapConfigDownloads(this IEndpointRouteBuilder app)
    {
        app.MapGet("/export/config/{nodeId:int}/{versionId:long}.txt", async (
            int nodeId, long versionId, bool? secrets, HttpContext http, ConfigHistoryService history, CancellationToken ct) =>
        {
            var text = await history.GetTextAsync(nodeId, versionId, http.User, secrets == true, ct);
            if (text is null)
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = "no-store";
            return Results.File(Encoding.UTF8.GetBytes(text.Text), "text/plain; charset=utf-8", $"config-node{nodeId}-v{versionId}{(text.Revealed ? "" : "-masked")}.txt");
        });

        app.MapGet("/export/config/{nodeId:int}.zip", async (
            int nodeId, bool? secrets, HttpContext http, ConfigHistoryService history, IDbContextFactory<YanmtDbContext> dbFactory,
            TimeProvider clock, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var name = await db.Nodes.AsNoTracking().Where(n => n.Id == nodeId).Select(n => n.Hostname).FirstOrDefaultAsync(ct);
            if (name is null)
            {
                return Results.NotFound();
            }

            var withSecrets = secrets == true && http.User.IsInRole(YanmtRoles.Admin);
            using var buffer = new MemoryStream();
            if (!await history.WriteZipAsync(nodeId, name, http.User, withSecrets, buffer, ct))
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = "no-store";
            var label = ConfigHistoryService.SafeName(name);
            return Results.File(buffer.ToArray(), "application/zip", $"{label}-config-history-{clock.GetUtcNow():yyyyMMdd}{(withSecrets ? "" : "-masked")}.zip");
        });
    }
}
