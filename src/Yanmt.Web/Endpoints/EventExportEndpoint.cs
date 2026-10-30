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
            if (!TryParseEnum<Severity>(state, out var st))
            {
                return Results.BadRequest($"Unknown state '{state}'. Use one of: {string.Join(", ", Enum.GetNames<Severity>())}.");
            }

            if (!TryParseEnum<StatusSourceType>(source, out var so))
            {
                return Results.BadRequest($"Unknown source '{source}'. Use one of: {string.Join(", ", Enum.GetNames<StatusSourceType>())}.");
            }

            var filter = new EventFilter(node, st, so, unacked);
            var csv = await events.ExportCsvAsync(filter, ct);
            var fileName = $"yanmt-events-{clock.GetUtcNow():yyyyMMdd}.csv";
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv", fileName);
        });

    /// <summary>Empty means no filter; anything else must be a defined name (numbers like "99" are rejected).</summary>
    private static bool TryParseEnum<T>(string? text, out T? value) where T : struct, Enum
    {
        value = null;
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        if (!int.TryParse(text, out _) && Enum.TryParse<T>(text, true, out var parsed) && Enum.IsDefined(parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }
}
