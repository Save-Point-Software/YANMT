using Yanmt.Core.Monitoring;
using Yanmt.Core.Status;

namespace Yanmt.Core.Tests.Monitoring;

public class IcmpEvaluatorTests
{
    private static readonly IcmpSettings Defaults = new(); // 4 echoes, warn at 50% loss or 150 ms, Critical after 3 failures

    private static IcmpMeasurement Reply(double avgMs, int received = 4) => new(4, received, avgMs);

    private static readonly IcmpMeasurement NoReply = new(4, 0, null);

    private static DampedState Run(DampedState start, params IcmpMeasurement[] polls)
    {
        var state = start;
        foreach (var poll in polls)
        {
            state = IcmpEvaluator.Evaluate(state, poll, Defaults).Next;
        }

        return state;
    }

    [Fact]
    public void All_echoes_answered_under_threshold_is_normal()
    {
        var e = IcmpEvaluator.Evaluate(DampedState.Unknown, Reply(3.25), Defaults);

        Assert.Equal(Severity.Normal, e.Next.State);
        Assert.Equal(3.25, e.Value);
        Assert.Equal("3.3 ms", e.ValueText);
    }

    [Fact]
    public void Latency_at_threshold_is_warning()
    {
        var e = IcmpEvaluator.Evaluate(DampedState.Unknown, Reply(150), Defaults);

        Assert.Equal(Severity.Warning, e.Next.State);
        Assert.Contains("threshold 150 ms", e.Reason);
        Assert.Equal("150 ms", e.ValueText);
    }

    [Fact]
    public void One_lost_echo_of_four_stays_normal_but_shows_the_loss()
    {
        var e = IcmpEvaluator.Evaluate(DampedState.Unknown, Reply(12, received: 3), Defaults);

        Assert.Equal(Severity.Normal, e.Next.State);
        Assert.Equal("12 ms, 25% loss", e.ValueText);
    }

    [Fact]
    public void Loss_at_threshold_is_warning()
    {
        var e = IcmpEvaluator.Evaluate(DampedState.Unknown, Reply(12, received: 2), Defaults);

        Assert.Equal(Severity.Warning, e.Next.State);
        Assert.Contains("50% loss", e.Reason);
    }

    [Fact]
    public void No_reply_is_warning_until_the_failure_count_then_critical()
    {
        var normal = new DampedState(Severity.Normal, null, 0);

        var first = Run(normal, NoReply);
        Assert.Equal(new DampedState(Severity.Warning, Severity.Critical, 1), first);

        var second = Run(first, NoReply);
        Assert.Equal(new DampedState(Severity.Warning, Severity.Critical, 2), second);

        var third = IcmpEvaluator.Evaluate(second, NoReply, Defaults);
        Assert.Equal(new DampedState(Severity.Critical, null, 0), third.Next);
        Assert.Equal("unreachable", third.ValueText);
        Assert.Null(third.Value);
    }

    [Fact]
    public void Critical_stays_critical_while_there_is_no_reply()
    {
        var critical = new DampedState(Severity.Critical, null, 0);

        Assert.Equal(Severity.Critical, Run(critical, NoReply, NoReply).State);
    }

    [Fact]
    public void Any_reply_clears_the_failure_count()
    {
        var state = Run(new DampedState(Severity.Normal, null, 0), NoReply, NoReply, Reply(2), NoReply, NoReply);

        // The reply reset the count, so two more failures are still short of three.
        Assert.Equal(new DampedState(Severity.Warning, Severity.Critical, 2), state);
    }

    [Fact]
    public void Recovery_from_critical_is_immediate()
    {
        Assert.Equal(Severity.Normal, Run(new DampedState(Severity.Critical, null, 0), Reply(2)).State);
    }

    [Fact]
    public void One_failure_before_critical_means_no_dampening()
    {
        var e = IcmpEvaluator.Evaluate(new DampedState(Severity.Normal, null, 0), NoReply, Defaults with { FailuresBeforeCritical = 1 });

        Assert.Equal(Severity.Critical, e.Next.State);
    }

    [Fact]
    public void Sub_millisecond_and_unknown_latency_format_cleanly()
    {
        Assert.Equal("0 ms", IcmpEvaluator.Evaluate(DampedState.Unknown, Reply(0), Defaults).ValueText);
        Assert.Equal("0.4 ms", IcmpEvaluator.Evaluate(DampedState.Unknown, Reply(0.4), Defaults).ValueText);
    }
}

public class IcmpSettingsTests
{
    [Fact]
    public void Defaults_are_valid() => Assert.Empty(new IcmpSettings().Validate());

    [Theory]
    [InlineData(nameof(IcmpSettings.IntervalSeconds))]
    [InlineData(nameof(IcmpSettings.EchoCount))]
    [InlineData(nameof(IcmpSettings.TimeoutMs))]
    [InlineData(nameof(IcmpSettings.WarningLossPercent))]
    [InlineData(nameof(IcmpSettings.WarningLatencyMs))]
    [InlineData(nameof(IcmpSettings.FailuresBeforeCritical))]
    public void Zero_is_out_of_range_for_every_field(string field)
    {
        var s = field switch
        {
            nameof(IcmpSettings.IntervalSeconds) => new IcmpSettings { IntervalSeconds = 0 },
            nameof(IcmpSettings.EchoCount) => new IcmpSettings { EchoCount = 0 },
            nameof(IcmpSettings.TimeoutMs) => new IcmpSettings { TimeoutMs = 0 },
            nameof(IcmpSettings.WarningLossPercent) => new IcmpSettings { WarningLossPercent = 0 },
            nameof(IcmpSettings.WarningLatencyMs) => new IcmpSettings { WarningLatencyMs = 0 },
            _ => new IcmpSettings { FailuresBeforeCritical = 0 },
        };

        Assert.Contains(s.Validate(), e => e.Field == field);
    }

    [Fact]
    public void A_burst_that_could_outlast_the_interval_is_refused()
    {
        // 10 echoes x 2 s timeout + spacing = 21.8 s worst case, longer than a 20 s interval.
        var errors = new IcmpSettings { IntervalSeconds = 20, EchoCount = 10, TimeoutMs = 2000 }.Validate();

        Assert.Equal(nameof(IcmpSettings.TimeoutMs), Assert.Single(errors).Field);
    }

    [Fact]
    public void Worst_case_burst_includes_the_spacing()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(4 * 2000 + 3 * IcmpSettings.EchoSpacingMs), new IcmpSettings().WorstCaseBurst);
    }
}
