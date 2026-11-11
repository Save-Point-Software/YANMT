using Yanmt.Core.Startup;

namespace Yanmt.Core.Tests.Startup;

public class StartupTimelineTests
{
    [Fact]
    public void Each_phase_takes_the_time_since_the_previous_mark()
    {
        var timeline = new StartupTimeline();

        Assert.Equal(TimeSpan.FromSeconds(0.6), timeline.Mark("runtime start", TimeSpan.FromSeconds(0.6)));
        Assert.Equal(TimeSpan.FromSeconds(0.2), timeline.Mark("configuration", TimeSpan.FromSeconds(0.8)));
        Assert.Equal(TimeSpan.FromSeconds(27.2), timeline.Mark("certificate", TimeSpan.FromSeconds(28)));

        Assert.Equal(TimeSpan.FromSeconds(28), timeline.Total);
        Assert.Equal("28.0 s total: runtime start 0.6 s, configuration 0.2 s, certificate 27.2 s", timeline.Summary());
    }

    [Fact]
    public void Clock_going_backwards_never_gives_a_negative_phase()
    {
        var timeline = new StartupTimeline();
        timeline.Mark("a", TimeSpan.FromSeconds(2));

        Assert.Equal(TimeSpan.Zero, timeline.Mark("b", TimeSpan.FromSeconds(1)));
        Assert.DoesNotContain("-", timeline.Summary());
    }

    [Fact]
    public void Empty_timeline_summarises_to_zero() => Assert.Equal("0.0 s total: ", new StartupTimeline().Summary());
}
