using System.Text;
using Microsoft.EntityFrameworkCore;
using Yanmt.Core.Maps;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Data.Entities;
using Yanmt.Infrastructure.Maps;
using Yanmt.Infrastructure.Tests.Monitoring;

namespace Yanmt.Infrastructure.Tests.Maps;

public class MapServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MapService Service(TestDatabase test) => new(test.Factory, test.Paths, test.Clock);

    private static byte[] Png(int width, int height)
    {
        var data = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 }.CopyTo(data, 0);
        "IHDR"u8.CopyTo(data.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), (uint)height);
        return data;
    }

    private static int Ok(MapResult result)
    {
        Assert.True(result.Ok, result.Error);
        return result.Id ?? 0;
    }

    [Fact]
    public async Task Home_map_is_created_once_and_reused()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = Service(test);

        var home = await svc.GetHomeMapIdAsync(Ct);

        Assert.Equal(home, await svc.GetHomeMapIdAsync(Ct));
        var map = await svc.GetAsync(home, Ct);
        Assert.Equal(MapService.DefaultMapName, map!.Name);
        Assert.Equal((MapRules.DefaultWidth, MapRules.DefaultHeight), (map.Width, map.Height));
    }

    [Fact]
    public async Task Submaps_nest_with_breadcrumbs_and_roll_up_worst_state()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = Service(test);
        var hub = await test.AddNodeAsync("lab-rtr-hub-01", "192.0.2.11");
        var spoke = await test.AddNodeAsync("lab-rtr-spoke-01", "192.0.2.21");
        var off = await test.AddNodeAsync("lab-rtr-off-01", "192.0.2.31", enabled: false);
        var home = await svc.GetHomeMapIdAsync(Ct);

        var siteIcon = Ok(await svc.CreateSubmapAsync(home, "Site A", 100, 100, Ct));
        var site = (await svc.GetAsync(home, Ct))!.Elements.Single(e => e.Id == siteIcon).TargetMapId!.Value;
        var closetIcon = Ok(await svc.CreateSubmapAsync(site, "Closet 1", 50, 50, Ct));
        var closet = (await svc.GetAsync(site, Ct))!.Elements.Single(e => e.Id == closetIcon).TargetMapId!.Value;
        Ok(await svc.AddNodeAsync(home, hub, 300, 300, Ct));
        Ok(await svc.AddNodeAsync(closet, spoke, 10, 10, Ct));
        Ok(await svc.AddNodeAsync(closet, off, 20, 20, Ct));

        var view = await svc.GetAsync(closet, Ct);
        Assert.Equal([MapService.DefaultMapName, "Site A", "Closet 1"], view!.Breadcrumbs.Select(c => c.Name));

        var contents = await svc.RollupContentsAsync(Ct);
        Assert.Equal([spoke], contents[closet].NodeIds); // the disabled node doesn't count
        var states = new Dictionary<int, Severity> { [hub] = Severity.Normal, [spoke] = Severity.Critical, [off] = Severity.Unknown };
        Assert.Equal(Severity.Critical, MapService.SubmapState(contents, site, id => states[id]));
        Assert.Equal(Severity.Critical, MapService.SubmapState(contents, home, id => states[id]));

        states[spoke] = Severity.Normal;
        Assert.Equal(Severity.Normal, MapService.SubmapState(contents, site, id => states[id]));
    }

    [Fact]
    public async Task A_cycle_of_submaps_is_allowed_and_rolls_up_without_looping()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = Service(test);
        var a = Ok(await svc.CreateAsync("Region A", null, Ct));
        var b = Ok(await svc.CreateAsync("Region B", null, Ct));
        Ok(await svc.AddSubmapAsync(a, b, 10, 10, Ct));
        Ok(await svc.AddSubmapAsync(b, a, 10, 10, Ct));

        Assert.False((await svc.AddSubmapAsync(a, a, 10, 10, Ct)).Ok);
        Assert.False((await svc.AddSubmapAsync(a, b, 20, 20, Ct)).Ok); // already there

        var contents = await svc.RollupContentsAsync(Ct);
        Assert.Equal(Severity.Unknown, MapService.SubmapState(contents, a, _ => Severity.Normal)); // nothing reporting
    }

    [Fact]
    public async Task A_node_appears_once_per_map_and_positions_are_clamped()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = Service(test);
        var node = await test.AddNodeAsync("lab-rtr-hub-01", "192.0.2.11");
        var home = await svc.GetHomeMapIdAsync(Ct);

        var el = Ok(await svc.AddNodeAsync(home, node, -40, 99999, Ct));
        var second = await svc.AddNodeAsync(home, node, 10, 10, Ct);

        Assert.False(second.Ok);
        Assert.Contains("already on this map", second.Error);
        var placed = (await svc.GetAsync(home, Ct))!.Elements.Single();
        Assert.Equal((0, MapRules.DefaultHeight), (placed.X, placed.Y));
        Assert.Empty(await svc.NodesNotOnMapAsync(home, Ct));

        Ok(await svc.MoveAsync(el, 123.456, 78.9, Ct));
        placed = (await svc.GetAsync(home, Ct))!.Elements.Single();
        Assert.Equal((123.5, 78.9), (placed.X, placed.Y));
    }

    [Fact]
    public async Task Links_need_two_distinct_icons_on_the_same_map_and_no_duplicates()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = Service(test);
        var home = await svc.GetHomeMapIdAsync(Ct);
        var other = Ok(await svc.CreateAsync("Elsewhere", null, Ct));
        var a = Ok(await svc.AddNodeAsync(home, await test.AddNodeAsync("lab-rtr-a", "192.0.2.1"), 10, 10, Ct));
        var b = Ok(await svc.AddNodeAsync(home, await test.AddNodeAsync("lab-rtr-b", "192.0.2.2"), 90, 90, Ct));
        var c = Ok(await svc.AddNodeAsync(other, await test.AddNodeAsync("lab-rtr-c", "192.0.2.3"), 50, 50, Ct));

        var link = Ok(await svc.AddLinkAsync(home, a, b, Ct));

        Assert.False((await svc.AddLinkAsync(home, b, a, Ct)).Ok); // same link, other direction
        Assert.False((await svc.AddLinkAsync(home, a, a, Ct)).Ok);
        Assert.False((await svc.AddLinkAsync(home, a, c, Ct)).Ok); // c is on another map
        Ok(await svc.SetLinkLabelAsync(link, "  Gi1/0/1 - Gi0/0/0  ", Ct));
        Assert.False((await svc.SetLinkLabelAsync(link, new string('x', MapRules.MaxLinkLabelLength + 1), Ct)).Ok);
        Assert.Equal("Gi1/0/1 - Gi0/0/0", (await svc.GetAsync(home, Ct))!.Links.Single().Label);
    }

    [Fact]
    public async Task Removing_an_icon_removes_its_links_and_both_are_audited()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = Service(test);
        var home = await svc.GetHomeMapIdAsync(Ct);
        var a = Ok(await svc.AddNodeAsync(home, await test.AddNodeAsync("lab-rtr-a", "192.0.2.1"), 10, 10, Ct));
        var b = Ok(await svc.AddNodeAsync(home, await test.AddNodeAsync("lab-rtr-b", "192.0.2.2"), 90, 90, Ct));
        Ok(await svc.AddLinkAsync(home, a, b, Ct));

        Ok(await svc.RemoveElementAsync(a, Ct));

        var view = await svc.GetAsync(home, Ct);
        Assert.Equal([b], view!.Elements.Select(e => e.Id));
        Assert.Empty(view.Links);
        await using var db = test.Open();
        var deletes = await db.AuditEntries.Where(x => x.Action == "Delete").Select(x => x.EntityType).ToListAsync(Ct);
        Assert.Equal(["MapElement", "MapLink"], deletes.Order());
        Assert.All(await db.AuditEntries.ToListAsync(Ct), x => Assert.Equal(@"LAB\alice", x.User));
    }

    [Fact]
    public async Task Deleting_a_node_removes_it_from_every_map()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = Service(test);
        var node = await test.AddNodeAsync("lab-rtr-hub-01", "192.0.2.11");
        var home = await svc.GetHomeMapIdAsync(Ct);
        var other = Ok(await svc.CreateAsync("Elsewhere", null, Ct));
        Ok(await svc.AddNodeAsync(home, node, 10, 10, Ct));
        Ok(await svc.AddNodeAsync(other, node, 10, 10, Ct));

        Assert.True(await new Yanmt.Infrastructure.Inventory.NodeService(test.Factory, test.Clock).DeleteAsync(node, Ct));

        Assert.Empty((await svc.GetAsync(home, Ct))!.Elements);
        Assert.Empty((await svc.GetAsync(other, Ct))!.Elements);
    }

    [Fact]
    public async Task Deleting_a_map_removes_its_submap_icons_and_the_home_map_is_protected()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = Service(test);
        var home = await svc.GetHomeMapIdAsync(Ct);
        var siteIcon = Ok(await svc.CreateSubmapAsync(home, "Site A", 10, 10, Ct));
        var site = (await svc.GetAsync(home, Ct))!.Elements.Single(e => e.Id == siteIcon).TargetMapId!.Value;
        var childIcon = Ok(await svc.CreateSubmapAsync(site, "Closet 1", 10, 10, Ct));
        var child = (await svc.GetAsync(site, Ct))!.Elements.Single(e => e.Id == childIcon).TargetMapId!.Value;

        Assert.False((await svc.DeleteAsync(home, Ct)).Ok);
        Ok(await svc.DeleteAsync(site, Ct));

        Assert.Null(await svc.GetAsync(site, Ct));
        Assert.Empty((await svc.GetAsync(home, Ct))!.Elements);
        var orphan = await svc.GetAsync(child, Ct);
        Assert.Null(orphan!.ParentMapId); // its submap survives as a top-level map
        Assert.Equal(home, await svc.GetHomeMapIdAsync(Ct));
    }

    [Fact]
    public async Task Map_names_are_unique_ignoring_case()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = Service(test);
        Ok(await svc.CreateAsync("Site A", null, Ct));

        var dup = await svc.CreateAsync("site a", null, Ct);

        Assert.False(dup.Ok);
        Assert.Contains("already exists", dup.Error);
        Assert.False((await svc.CreateAsync("   ", null, Ct)).Ok);
    }

    [Fact]
    public async Task Png_background_sets_the_canvas_size_and_replacing_it_removes_the_old_file()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = Service(test);
        var home = await svc.GetHomeMapIdAsync(Ct);
        var el = Ok(await svc.AddNodeAsync(home, await test.AddNodeAsync("lab-rtr-hub-01", "192.0.2.11"), 1500, 900, Ct));

        Ok(await svc.SetBackgroundAsync(home, new MemoryStream(Png(1200, 800)), Ct));

        var view = await svc.GetAsync(home, Ct);
        Assert.Equal((1200, 800), (view!.Width, view.Height));
        Assert.Equal((1200, 800), (view.Elements.Single(e => e.Id == el).X, view.Elements.Single().Y)); // pulled onto the canvas
        Assert.StartsWith($"maps/{home}/background?v=", view.BackgroundUrl);
        var first = Assert.Single(Directory.GetFiles(test.Paths.MapImages));
        Assert.EndsWith(".png", first);
        Assert.Equal("image/png", (await svc.GetBackgroundFileAsync(home, Ct))!.Value.ContentType);

        Ok(await svc.SetBackgroundAsync(home, new MemoryStream(Png(2000, 1500)), Ct));
        var second = Assert.Single(Directory.GetFiles(test.Paths.MapImages));
        Assert.NotEqual(first, second);

        Ok(await svc.ClearBackgroundAsync(home, Ct));
        Assert.Empty(Directory.GetFiles(test.Paths.MapImages));
        Assert.Null(await svc.GetBackgroundFileAsync(home, Ct));
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"/>")]
    [InlineData("GIF8 but not really")]
    public async Task Non_raster_backgrounds_are_refused(string content)
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = Service(test);
        var home = await svc.GetHomeMapIdAsync(Ct);

        var result = await svc.SetBackgroundAsync(home, new MemoryStream(Encoding.UTF8.GetBytes(content)), Ct);

        Assert.False(result.Ok);
        Assert.Contains("PNG, JPEG or GIF", result.Error);
        Assert.False(Directory.Exists(test.Paths.MapImages) && Directory.GetFiles(test.Paths.MapImages).Length > 0);
    }

    [Fact]
    public async Task Oversized_backgrounds_are_refused()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = Service(test);
        var home = await svc.GetHomeMapIdAsync(Ct);
        var big = new byte[MapRules.MaxBackgroundBytes + 1];
        Png(800, 600).CopyTo(big, 0);

        Assert.False((await svc.SetBackgroundAsync(home, new MemoryStream(big), Ct)).Ok);
        Assert.False((await svc.SetBackgroundAsync(home, new MemoryStream(Png(12000, 600)), Ct)).Ok);
    }

    [Fact]
    public async Task Label_source_is_stored_for_node_icons_only()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = Service(test);
        var home = await svc.GetHomeMapIdAsync(Ct);
        var el = Ok(await svc.AddNodeAsync(home, await test.AddNodeAsync("lab-rtr-hub-01", "192.0.2.11"), 10, 10, Ct));
        var sub = Ok(await svc.CreateSubmapAsync(home, "Site A", 50, 50, Ct));

        Ok(await svc.SetLabelSourceAsync(el, StatusSourceType.Icmp, null, Ct));
        Assert.False((await svc.SetLabelSourceAsync(sub, StatusSourceType.Icmp, null, Ct)).Ok);
        Assert.False((await svc.SetLabelSourceAsync(el, (StatusSourceType)99, null, Ct)).Ok);

        var placed = (await svc.GetAsync(home, Ct))!.Elements.Single(e => e.Id == el);
        Assert.Equal((StatusSourceType.Icmp, ""), (placed.LabelSourceType!.Value, placed.LabelSourceKey));

        Ok(await svc.SetLabelSourceAsync(el, null, null, Ct));
        Assert.Null((await svc.GetAsync(home, Ct))!.Elements.Single(e => e.Id == el).LabelSourceType);
    }

    [Fact]
    public async Task Shrinking_the_canvas_pulls_icons_inside()
    {
        await using var test = await TestDatabase.CreateAsync();
        var svc = Service(test);
        var home = await svc.GetHomeMapIdAsync(Ct);
        Ok(await svc.AddNodeAsync(home, await test.AddNodeAsync("lab-rtr-hub-01", "192.0.2.11"), 1500, 950, Ct));

        Ok(await svc.SetCanvasAsync(home, 800, 600, Ct));
        Assert.False((await svc.SetCanvasAsync(home, 100, 600, Ct)).Ok);

        var placed = (await svc.GetAsync(home, Ct))!.Elements.Single();
        Assert.Equal((800, 600), (placed.X, placed.Y));
    }
}
