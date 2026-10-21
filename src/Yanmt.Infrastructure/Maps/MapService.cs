using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yanmt.Core.Inventory;
using Yanmt.Core.Maps;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Data.Entities;

namespace Yanmt.Infrastructure.Maps;

/// <summary>Maps, their icons and links, and background images. Every write goes through the audit interceptor.</summary>
public sealed class MapService(IDbContextFactory<YanmtDbContext> dbFactory, YanmtPaths paths, TimeProvider clock)
{
    public const string DefaultMapName = "Network";

    /// <summary>The map shown at "/": the oldest top-level map, created on first use if there are none.</summary>
    public async Task<int> GetHomeMapIdAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var id = await HomeIdAsync(db, cancellationToken);
        if (id is not null)
        {
            return id.Value;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var map = new Map { Name = DefaultMapName, CreatedUtc = now, UpdatedUtc = now };
        db.Maps.Add(map);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return map.Id;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            // Two first visits at once: the other one created it.
            await using var fresh = await dbFactory.CreateDbContextAsync(cancellationToken);
            return (await HomeIdAsync(fresh, cancellationToken))!.Value;
        }
    }

    private static async Task<int?> HomeIdAsync(YanmtDbContext db, CancellationToken cancellationToken) =>
        await db.Maps.Where(m => m.ParentMapId == null).OrderBy(m => m.Id).Select(m => (int?)m.Id).FirstOrDefaultAsync(cancellationToken)
        ?? await db.Maps.OrderBy(m => m.Id).Select(m => (int?)m.Id).FirstOrDefaultAsync(cancellationToken);

    public async Task<List<MapSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Maps.AsNoTracking()
            .OrderBy(m => m.Name)
            .Select(m => new MapSummary(m.Id, m.Name, m.ParentMapId,
                m.Elements.Count(e => e.Kind == MapElementKind.Node), m.Elements.Count(e => e.Kind == MapElementKind.Submap)))
            .ToListAsync(cancellationToken);
    }

    public async Task<MapView?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var map = await db.Maps.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (map is null)
        {
            return null;
        }

        var elements = await db.MapElements.AsNoTracking()
            .Where(e => e.MapId == id)
            .OrderBy(e => e.Id)
            .Select(e => new MapElementView(
                e.Id, e.Kind, e.X, e.Y,
                e.NodeId, e.Node!.Hostname, e.Node.ManagementIp,
                (NodeIconType?)e.Node.Icon ?? NodeIconType.Generic, (bool?)e.Node.Enabled ?? false,
                e.TargetMapId, e.TargetMap!.Name,
                e.LabelSourceType, e.LabelSourceKey))
            .ToListAsync(cancellationToken);
        var links = await db.MapLinks.AsNoTracking()
            .Where(l => l.MapId == id)
            .OrderBy(l => l.Id)
            .Select(l => new MapLinkView(l.Id, l.FromElementId, l.ToElementId, l.Label))
            .ToListAsync(cancellationToken);

        var background = map.BackgroundImage is null ? null : $"maps/{map.Id}/background?v={Uri.EscapeDataString(map.BackgroundImage)}";
        return new MapView(map.Id, map.Name, map.ParentMapId, background, map.Width, map.Height, elements, links,
            await BreadcrumbsAsync(db, map, cancellationToken));
    }

    /// <summary>Parent chain from the top down to this map. Parents are user-editable, so cycles are cut.</summary>
    private static async Task<List<MapCrumb>> BreadcrumbsAsync(YanmtDbContext db, Map map, CancellationToken cancellationToken)
    {
        var all = await db.Maps.AsNoTracking().Select(m => new { m.Id, m.Name, m.ParentMapId }).ToDictionaryAsync(m => m.Id, cancellationToken);
        var crumbs = new List<MapCrumb>();
        var seen = new HashSet<int> { map.Id };
        for (var parent = map.ParentMapId; parent is { } pid && all.TryGetValue(pid, out var p) && seen.Add(pid); parent = p.ParentMapId)
        {
            crumbs.Insert(0, new MapCrumb(p.Id, p.Name));
        }

        crumbs.Add(new MapCrumb(map.Id, map.Name));
        return crumbs;
    }

    /// <summary>
    /// What each map contains, for submap rollups. Disabled nodes are left out: they're deliberately
    /// unmonitored, so they shouldn't turn a whole site gray.
    /// </summary>
    public async Task<Dictionary<int, MapContents>> RollupContentsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var elements = await db.MapElements.AsNoTracking()
            .Where(e => e.Kind == MapElementKind.Submap || e.Node!.Enabled)
            .Select(e => new { e.MapId, e.Kind, e.NodeId, e.TargetMapId })
            .ToListAsync(cancellationToken);
        var mapIds = await db.Maps.Select(m => m.Id).ToListAsync(cancellationToken);
        var byMap = elements.ToLookup(e => e.MapId);
        return mapIds.ToDictionary(id => id, id => new MapContents(
            byMap[id].Where(e => e.Kind == MapElementKind.Node).Select(e => e.NodeId!.Value).ToList(),
            byMap[id].Where(e => e.Kind == MapElementKind.Submap).Select(e => e.TargetMapId!.Value).ToList()));
    }

    /// <summary>Rolled-up state of a submap from the current node states.</summary>
    public static Severity SubmapState(IReadOnlyDictionary<int, MapContents> contents, int mapId, Func<int, Severity> nodeState) =>
        new MapRollup(contents, nodeState).StateOf(mapId);

    public async Task<MapResult> CreateAsync(string name, int? parentMapId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var map = NewMap(name, parentMapId, out var error);
        if (map is null)
        {
            return MapResult.Fail(error!);
        }

        db.Maps.Add(map);
        return await SaveAsync(db, () => map.Id, $"A map named '{map.Name}' already exists.", cancellationToken);
    }

    /// <summary>Creates a new map and places it as a submap icon on <paramref name="mapId"/>.</summary>
    public async Task<MapResult> CreateSubmapAsync(int mapId, string name, double x, double y, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var parent = await db.Maps.FirstOrDefaultAsync(m => m.Id == mapId, cancellationToken);
        if (parent is null)
        {
            return MapResult.Fail("The map no longer exists.");
        }

        var map = NewMap(name, mapId, out var error);
        if (map is null)
        {
            return MapResult.Fail(error!);
        }

        var (cx, cy) = MapRules.Clamp(x, y, parent.Width, parent.Height);
        var element = new MapElement { Map = parent, Kind = MapElementKind.Submap, TargetMap = map, X = cx, Y = cy };
        db.Maps.Add(map);
        db.MapElements.Add(element);
        Touch(parent);
        return await SaveAsync(db, () => element.Id, $"A map named '{map.Name}' already exists.", cancellationToken);
    }

    private Map? NewMap(string name, int? parentMapId, out string? error)
    {
        error = MapRules.ValidateName(name);
        if (error is not null)
        {
            return null;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        return new Map { Name = name.Trim(), ParentMapId = parentMapId, CreatedUtc = now, UpdatedUtc = now };
    }

    public async Task<MapResult> RenameAsync(int id, string name, CancellationToken cancellationToken = default)
    {
        if (MapRules.ValidateName(name) is { } error)
        {
            return MapResult.Fail(error);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var map = await db.Maps.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (map is null)
        {
            return MapResult.Fail("The map no longer exists.");
        }

        map.Name = name.Trim();
        Touch(map);
        return await SaveAsync(db, () => map.Id, $"A map named '{map.Name}' already exists.", cancellationToken);
    }

    public async Task<MapResult> SetCanvasAsync(int id, int width, int height, CancellationToken cancellationToken = default)
    {
        if (MapRules.ValidateCanvas(width, height) is { } error)
        {
            return MapResult.Fail(error);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var map = await db.Maps.Include(m => m.Elements).FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (map is null)
        {
            return MapResult.Fail("The map no longer exists.");
        }

        map.Width = width;
        map.Height = height;
        ClampElements(map);
        Touch(map);
        await db.SaveChangesAsync(cancellationToken);
        return MapResult.Success(map.Id);
    }

    /// <summary>
    /// Deletes a map with its icons and links. Submap icons pointing at it disappear from other
    /// maps; its own submaps become top-level maps. The home map can't be deleted.
    /// </summary>
    public async Task<MapResult> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await HomeIdAsync(db, cancellationToken) == id)
        {
            return MapResult.Fail("This is the home map, so it can't be deleted. Rename it instead.");
        }

        var map = await db.Maps.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (map is null)
        {
            return MapResult.Fail("The map no longer exists.");
        }

        var background = map.BackgroundImage;
        db.Maps.Remove(map);
        await db.SaveChangesAsync(cancellationToken);
        DeleteImageFile(background);
        return MapResult.Success();
    }

    /// <summary>
    /// Stores an uploaded background. The type is taken from the file's magic bytes (PNG, JPEG or
    /// GIF only) and the canvas is resized to the image so positions line up with the picture.
    /// </summary>
    public async Task<MapResult> SetBackgroundAsync(int id, Stream content, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MapRules.MaxBackgroundBytes)
            {
                return MapResult.Fail($"The image is larger than {MapRules.MaxBackgroundBytes / (1024 * 1024)} MB.");
            }

            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();
        if (!ImageInfo.TryRead(bytes, out var format, out var width, out var height))
        {
            return MapResult.Fail("That isn't a PNG, JPEG or GIF image. (SVG isn't accepted.)");
        }

        if (width > MapRules.MaxCanvasSize || height > MapRules.MaxCanvasSize)
        {
            return MapResult.Fail($"The image is {width}×{height}; the largest supported is {MapRules.MaxCanvasSize}×{MapRules.MaxCanvasSize} pixels.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var map = await db.Maps.Include(m => m.Elements).FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (map is null)
        {
            return MapResult.Fail("The map no longer exists.");
        }

        var fileName = $"{map.Id}-{Guid.NewGuid():N}{ImageInfo.Extension(format)}";
        Directory.CreateDirectory(paths.MapImages);
        await File.WriteAllBytesAsync(Path.Combine(paths.MapImages, fileName), bytes, cancellationToken);

        var old = map.BackgroundImage;
        map.BackgroundImage = fileName;
        map.Width = Math.Max(width, MapRules.MinCanvasSize);
        map.Height = Math.Max(height, MapRules.MinCanvasSize);
        ClampElements(map);
        Touch(map);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            DeleteImageFile(fileName);
            throw;
        }

        DeleteImageFile(old);
        return MapResult.Success(map.Id);
    }

    public async Task<MapResult> ClearBackgroundAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var map = await db.Maps.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (map is null)
        {
            return MapResult.Fail("The map no longer exists.");
        }

        var old = map.BackgroundImage;
        map.BackgroundImage = null;
        Touch(map);
        await db.SaveChangesAsync(cancellationToken);
        DeleteImageFile(old);
        return MapResult.Success(map.Id);
    }

    /// <summary>The background file for a map, or null. Only names YANMT generated are ever resolved.</summary>
    public async Task<(string Path, string ContentType)?> GetBackgroundFileAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var name = await db.Maps.Where(m => m.Id == id).Select(m => m.BackgroundImage).FirstOrDefaultAsync(cancellationToken);
        if (name is null || Path.GetFileName(name) != name)
        {
            return null;
        }

        var path = Path.Combine(paths.MapImages, name);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var file = File.OpenRead(path);
        var header = new byte[64 * 1024];
        var length = await file.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        return ImageInfo.TryRead(header.AsSpan(0, length), out var format, out _, out _) ? (path, ImageInfo.ContentType(format)) : null;
    }

    /// <summary>Enabled and disabled nodes not yet on this map, for the "Add node" picker.</summary>
    public async Task<List<NodeChoice>> NodesNotOnMapAsync(int mapId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Nodes.AsNoTracking()
            .Where(n => !db.MapElements.Any(e => e.MapId == mapId && e.NodeId == n.Id))
            .OrderBy(n => n.Hostname)
            .Select(n => new NodeChoice(n.Id, n.Hostname, n.ManagementIp, n.Site))
            .ToListAsync(cancellationToken);
    }

    public async Task<MapResult> AddNodeAsync(int mapId, int nodeId, double x, double y, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var map = await db.Maps.FirstOrDefaultAsync(m => m.Id == mapId, cancellationToken);
        if (map is null || !await db.Nodes.AnyAsync(n => n.Id == nodeId, cancellationToken))
        {
            return MapResult.Fail("The map or node no longer exists.");
        }

        var (cx, cy) = MapRules.Clamp(x, y, map.Width, map.Height);
        var element = new MapElement { MapId = mapId, Kind = MapElementKind.Node, NodeId = nodeId, X = cx, Y = cy };
        db.MapElements.Add(element);
        Touch(map);
        return await SaveAsync(db, () => element.Id, "That node is already on this map.", cancellationToken);
    }

    /// <summary>Places an existing map as a submap icon. A map can't contain itself (deeper cycles are cut by the rollup).</summary>
    public async Task<MapResult> AddSubmapAsync(int mapId, int targetMapId, double x, double y, CancellationToken cancellationToken = default)
    {
        if (mapId == targetMapId)
        {
            return MapResult.Fail("A map can't contain itself.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var map = await db.Maps.FirstOrDefaultAsync(m => m.Id == mapId, cancellationToken);
        if (map is null || !await db.Maps.AnyAsync(m => m.Id == targetMapId, cancellationToken))
        {
            return MapResult.Fail("The map no longer exists.");
        }

        var (cx, cy) = MapRules.Clamp(x, y, map.Width, map.Height);
        var element = new MapElement { MapId = mapId, Kind = MapElementKind.Submap, TargetMapId = targetMapId, X = cx, Y = cy };
        db.MapElements.Add(element);
        Touch(map);
        return await SaveAsync(db, () => element.Id, "That submap is already on this map.", cancellationToken);
    }

    public async Task<MapResult> MoveAsync(int elementId, double x, double y, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var element = await db.MapElements.Include(e => e.Map).FirstOrDefaultAsync(e => e.Id == elementId, cancellationToken);
        if (element is null)
        {
            return MapResult.Fail("That icon no longer exists.");
        }

        (element.X, element.Y) = MapRules.Clamp(x, y, element.Map.Width, element.Map.Height);
        await db.SaveChangesAsync(cancellationToken);
        return MapResult.Success(element.Id);
    }

    public async Task<MapResult> RemoveElementAsync(int elementId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var element = await db.MapElements.Include(e => e.Map).FirstOrDefaultAsync(e => e.Id == elementId, cancellationToken);
        if (element is null)
        {
            return MapResult.Fail("That icon no longer exists.");
        }

        // Load its links so their removal is audited too (the DB would cascade them silently).
        var links = await db.MapLinks.Where(l => l.FromElementId == elementId || l.ToElementId == elementId).ToListAsync(cancellationToken);
        db.MapLinks.RemoveRange(links);
        db.MapElements.Remove(element);
        Touch(element.Map);
        await db.SaveChangesAsync(cancellationToken);
        return MapResult.Success();
    }

    /// <summary>Chooses the status value shown under a node's name; null type shows none.</summary>
    public async Task<MapResult> SetLabelSourceAsync(int elementId, StatusSourceType? type, string? key, CancellationToken cancellationToken = default)
    {
        if (type is { } t && !Enum.IsDefined(t) || key?.Length > 64)
        {
            return MapResult.Fail("Unknown label value.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var element = await db.MapElements.FirstOrDefaultAsync(e => e.Id == elementId, cancellationToken);
        if (element is null || element.Kind != MapElementKind.Node)
        {
            return MapResult.Fail("That node icon no longer exists.");
        }

        element.LabelSourceType = type;
        element.LabelSourceKey = type is null ? null : key ?? "";
        await db.SaveChangesAsync(cancellationToken);
        return MapResult.Success(element.Id);
    }

    public async Task<MapResult> AddLinkAsync(int mapId, int fromElementId, int toElementId, CancellationToken cancellationToken = default)
    {
        if (fromElementId == toElementId)
        {
            return MapResult.Fail("A link needs two different icons.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var onMap = await db.MapElements.CountAsync(e => e.MapId == mapId && (e.Id == fromElementId || e.Id == toElementId), cancellationToken);
        if (onMap != 2)
        {
            return MapResult.Fail("Both icons must be on this map.");
        }

        if (await db.MapLinks.AnyAsync(l => l.MapId == mapId
                && ((l.FromElementId == fromElementId && l.ToElementId == toElementId)
                    || (l.FromElementId == toElementId && l.ToElementId == fromElementId)), cancellationToken))
        {
            return MapResult.Fail("Those icons are already linked.");
        }

        var link = new MapLink { MapId = mapId, FromElementId = fromElementId, ToElementId = toElementId };
        db.MapLinks.Add(link);
        return await SaveAsync(db, () => link.Id, "Those icons are already linked.", cancellationToken);
    }

    public async Task<MapResult> SetLinkLabelAsync(int linkId, string? label, CancellationToken cancellationToken = default)
    {
        label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (MapRules.ValidateLinkLabel(label) is { } error)
        {
            return MapResult.Fail(error);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var link = await db.MapLinks.FirstOrDefaultAsync(l => l.Id == linkId, cancellationToken);
        if (link is null)
        {
            return MapResult.Fail("That link no longer exists.");
        }

        link.Label = label;
        await db.SaveChangesAsync(cancellationToken);
        return MapResult.Success(link.Id);
    }

    public async Task<MapResult> RemoveLinkAsync(int linkId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var link = await db.MapLinks.FirstOrDefaultAsync(l => l.Id == linkId, cancellationToken);
        if (link is null)
        {
            return MapResult.Fail("That link no longer exists.");
        }

        db.MapLinks.Remove(link);
        await db.SaveChangesAsync(cancellationToken);
        return MapResult.Success();
    }

    private static void ClampElements(Map map)
    {
        foreach (var e in map.Elements)
        {
            var (x, y) = MapRules.Clamp(e.X, e.Y, map.Width, map.Height);
            if (x != e.X || y != e.Y)
            {
                (e.X, e.Y) = (x, y);
            }
        }
    }

    private void Touch(Map map) => map.UpdatedUtc = clock.GetUtcNow().UtcDateTime;

    private static async Task<MapResult> SaveAsync(YanmtDbContext db, Func<int> id, string conflict, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return MapResult.Success(id());
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            return MapResult.Fail(conflict);
        }
    }

    private void DeleteImageFile(string? name)
    {
        if (name is null || Path.GetFileName(name) != name)
        {
            return;
        }

        try
        {
            File.Delete(Path.Combine(paths.MapImages, name));
        }
        catch (IOException)
        {
            // Best effort: an orphaned image is harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
