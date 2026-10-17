using System.Text;
using Yanmt.Infrastructure.Inventory;

namespace Yanmt.Web.Endpoints;

public static class NodeExportEndpoint
{
    /// <summary>GET /export/nodes.csv — viewer access (the fallback policy). The export has no secret columns.</summary>
    public static void MapNodeExport(this IEndpointRouteBuilder app) =>
        app.MapGet("/export/nodes.csv", async (NodeService nodes, TimeProvider clock, CancellationToken ct) =>
        {
            var csv = await nodes.ExportCsvAsync(ct);
            var fileName = $"yanmt-nodes-{clock.GetUtcNow():yyyyMMdd}.csv";

            // UTF-8 with BOM so Excel opens non-ASCII site names correctly.
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv", fileName);
        });
}
