using System.Text;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Data.Entities;
using Yanmt.Infrastructure.Events;

namespace Yanmt.Web.Endpoints;

public static class EventExportEndpoint
{
    /// <summary>
    /// GET /export/events.csv?node=&amp;state=&amp;source=&amp;unacked=true — viewer access (the fallback policy).
    /// Takes the same filters as the events page. The export has no secret columns.
    /// </summary>
    public static void MapEventExport(this IEndpointRouteBuilder app) =>
        app.MapGet("/export/events.csv", async (
            EventService events, TimeProvider clock, CancellationToken ct,
            int? node, string? state, string? source, bool unacked = false) =>
        {
            var filter = new EventFilter(
                node,
                Enum.TryParse<Severity>(state, true, out var st) && Enum.IsDefined(st) ? st : null,
                Enum.TryParse<StatusSourceType>(source, true, out var so) && Enum.IsDefined(so) ? so : null,
                unacked);
            var csv = await events.ExportCsvAsync(filter, ct);
            var fileName = $"yanmt-events-{clock.GetUtcNow():yyyyMMdd}.csv";
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv", fileName);
        });
}
