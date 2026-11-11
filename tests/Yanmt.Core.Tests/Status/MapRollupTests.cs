using Yanmt.Core.Status;

namespace Yanmt.Core.Tests.Status;

public class MapRollupTests
{
    // Node ids map to colors for these tests.
    private static readonly Dictionary<int, NodeColor> NodeStates = new()
    {
        [1] = NodeColor.Normal,
        [2] = NodeColor.Normal,
        [3] = NodeColor.Attention,
        [4] = NodeColor.Down,
        [5] = NodeColor.NoData,
        [6] = NodeColor.NoSnmp,
    };

    private static MapRollup Rollup(Dictionary<int, MapContents> maps) => new(maps, id => NodeStates[id]);

    private static MapContents Map(int[] nodes, int[]? submaps = null) => new(nodes, submaps ?? []);

    [Fact]
    public void Map_shows_worst_node()
    {
        var rollup = Rollup(new() { [10] = Map([1, 2, 3]) });
        Assert.Equal(NodeColor.Attention, rollup.StateOf(10));
    }

    [Fact]
    public void Nested_submap_state_bubbles_up()
    {
        // root(10) -> site A(11) -> closet(12) which has a Critical node
        var rollup = Rollup(new()
        {
            [10] = Map([1], [11]),
            [11] = Map([2], [12]),
            [12] = Map([4]),
        });

        Assert.Equal(NodeColor.Down, rollup.StateOf(10));
        Assert.Equal(NodeColor.Down, rollup.StateOf(11));
        Assert.Equal(NodeColor.Down, rollup.StateOf(12));
    }

    [Fact]
    public void Empty_map_is_gray()
    {
        var rollup = Rollup(new() { [10] = Map([]) });
        Assert.Equal(NodeColor.NoData, rollup.StateOf(10));
    }

    [Fact]
    public void Missing_submap_reference_adds_nothing()
    {
        var rollup = Rollup(new() { [10] = Map([1], [99]) });
        Assert.Equal(NodeColor.Normal, rollup.StateOf(10));
    }

    [Fact]
    public void Gray_nodes_do_not_hide_green()
    {
        var rollup = Rollup(new() { [10] = Map([1, 5]) });
        Assert.Equal(NodeColor.Normal, rollup.StateOf(10));
    }

    [Fact]
    public void Red_beats_orange_beats_yellow()
    {
        var rollup = Rollup(new()
        {
            [10] = Map([3, 6]),
            [11] = Map([3, 6, 4]),
            [12] = Map([1, 3]),
        });

        Assert.Equal(NodeColor.NoSnmp, rollup.StateOf(10));
        Assert.Equal(NodeColor.Down, rollup.StateOf(11));
        Assert.Equal(NodeColor.Attention, rollup.StateOf(12));
    }

    [Fact]
    public void Cycle_terminates_and_includes_everything_reachable()
    {
        // 10 -> 11 -> 12 -> 10 (cycle); the Warning node lives in 12
        var rollup = Rollup(new()
        {
            [10] = Map([1], [11]),
            [11] = Map([2], [12]),
            [12] = Map([3], [10]),
        });

        Assert.Equal(NodeColor.Attention, rollup.StateOf(10));
        Assert.Equal(NodeColor.Attention, rollup.StateOf(11));
        Assert.Equal(NodeColor.Attention, rollup.StateOf(12));
    }

    [Fact]
    public void Self_referencing_map_terminates()
    {
        var rollup = Rollup(new() { [10] = Map([2], [10]) });
        Assert.Equal(NodeColor.Normal, rollup.StateOf(10));
    }

    [Fact]
    public void Cycle_results_do_not_depend_on_query_order()
    {
        var maps = new Dictionary<int, MapContents>
        {
            [10] = Map([1], [11]),
            [11] = Map([4], [10]),
        };

        var a = Rollup(maps);
        var first = (a.StateOf(11), a.StateOf(10));
        var b = Rollup(maps);
        var second = (b.StateOf(10), b.StateOf(11));

        Assert.Equal((NodeColor.Down, NodeColor.Down), first);
        Assert.Equal((NodeColor.Down, NodeColor.Down), second);
    }

    [Fact]
    public void Shared_submap_is_counted_from_both_parents()
    {
        var rollup = Rollup(new()
        {
            [10] = Map([], [11, 12]),
            [11] = Map([1], [13]),
            [12] = Map([2], [13]),
            [13] = Map([6]),
        });

        Assert.Equal(NodeColor.NoSnmp, rollup.StateOf(10));
    }
}
