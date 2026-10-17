using Yanmt.Core.Status;

namespace Yanmt.Core.Tests.Status;

public class MapRollupTests
{
    // Node ids map to states for these tests.
    private static readonly Dictionary<int, Severity> NodeStates = new()
    {
        [1] = Severity.Normal,
        [2] = Severity.Normal,
        [3] = Severity.Warning,
        [4] = Severity.Critical,
        [5] = Severity.Unknown,
    };

    private static MapRollup Rollup(Dictionary<int, MapContents> maps) => new(maps, id => NodeStates[id]);

    private static MapContents Map(int[] nodes, int[]? submaps = null) => new(nodes, submaps ?? []);

    [Fact]
    public void Map_shows_worst_node()
    {
        var rollup = Rollup(new() { [10] = Map([1, 2, 3]) });
        Assert.Equal(Severity.Warning, rollup.StateOf(10));
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

        Assert.Equal(Severity.Critical, rollup.StateOf(10));
        Assert.Equal(Severity.Critical, rollup.StateOf(11));
        Assert.Equal(Severity.Critical, rollup.StateOf(12));
    }

    [Fact]
    public void Empty_map_is_Unknown()
    {
        var rollup = Rollup(new() { [10] = Map([]) });
        Assert.Equal(Severity.Unknown, rollup.StateOf(10));
    }

    [Fact]
    public void Missing_submap_reference_counts_as_Unknown()
    {
        var rollup = Rollup(new() { [10] = Map([1], [99]) });
        Assert.Equal(Severity.Unknown, rollup.StateOf(10));
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

        Assert.Equal(Severity.Warning, rollup.StateOf(10));
        Assert.Equal(Severity.Warning, rollup.StateOf(11));
        Assert.Equal(Severity.Warning, rollup.StateOf(12));
    }

    [Fact]
    public void Self_referencing_map_terminates()
    {
        var rollup = Rollup(new() { [10] = Map([2], [10]) });
        Assert.Equal(Severity.Normal, rollup.StateOf(10));
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

        Assert.Equal((Severity.Critical, Severity.Critical), first);
        Assert.Equal((Severity.Critical, Severity.Critical), second);
    }

    [Fact]
    public void Shared_submap_is_counted_from_both_parents()
    {
        var rollup = Rollup(new()
        {
            [10] = Map([], [11, 12]),
            [11] = Map([1], [13]),
            [12] = Map([2], [13]),
            [13] = Map([5]),
        });

        Assert.Equal(Severity.Unknown, rollup.StateOf(10));
    }
}
