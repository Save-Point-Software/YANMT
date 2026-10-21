using Yanmt.Infrastructure.Maps;

namespace Yanmt.Web.Endpoints;

public static class MapBackgroundEndpoint
{
    /// <summary>
    /// GET /maps/{id}/background: viewer access (the fallback policy). The content type comes from
    /// the file's magic bytes, and nosniff stops a browser from treating it as anything else.
    /// The URL carries the file name, so a new upload busts the cache.
    /// </summary>
    public static void MapMapBackground(this IEndpointRouteBuilder app) =>
        app.MapGet("/maps/{id:int}/background", async (int id, MapService maps, HttpContext http, CancellationToken ct) =>
        {
            var file = await maps.GetBackgroundFileAsync(id, ct);
            if (file is null)
            {
                return Results.NotFound();
            }

            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(file.Value.Path, file.Value.ContentType);
        });
}
