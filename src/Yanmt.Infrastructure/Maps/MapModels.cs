using Yanmt.Core.Inventory;
using Yanmt.Infrastructure.Data.Entities;

namespace Yanmt.Infrastructure.Maps;

public sealed record MapSummary(int Id, string Name, int? ParentMapId, int NodeCount, int SubmapCount);

public sealed record MapCrumb(int Id, string Name);

/// <summary>An icon on a map, with what's needed to draw it. Node fields are null for submaps and vice versa.</summary>
public sealed record MapElementView(
    int Id,
    MapElementKind Kind,
    double X,
    double Y,
    int? NodeId,
    string? Hostname,
    string? ManagementIp,
    NodeIconType Icon,
    bool NodeEnabled,
    int? TargetMapId,
    string? TargetMapName,
    StatusSourceType? LabelSourceType,
    string? LabelSourceKey)
{
    public string Title => Kind == MapElementKind.Node ? Hostname ?? "?" : TargetMapName ?? "?";
}

/// <summary>A link. When bound, BoundNodeId/BoundInstanceId locate the monitor instance whose state colors it.</summary>
public sealed record MapLinkView(int Id, int FromElementId, int ToElementId, string? Label, int? BoundInstanceId = null, int? BoundNodeId = null, bool ShowBoundValue = false);

/// <summary>A monitor instance a link can be bound to: one on either end node.</summary>
public sealed record LinkBindingChoice(int InstanceId, int NodeId, string Hostname, string Name);

public sealed record MapView(
    int Id,
    string Name,
    int? ParentMapId,
    string? BackgroundUrl,
    int Width,
    int Height,
    IReadOnlyList<MapElementView> Elements,
    IReadOnlyList<MapLinkView> Links,
    IReadOnlyList<MapCrumb> Breadcrumbs);

public sealed record NodeChoice(int Id, string Hostname, string ManagementIp, string? Site);

public sealed record MapResult(bool Ok, string? Error = null, int? Id = null)
{
    public static MapResult Success(int? id = null) => new(true, null, id);

    public static MapResult Fail(string error) => new(false, error);
}
