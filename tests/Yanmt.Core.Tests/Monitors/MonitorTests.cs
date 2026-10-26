using Yanmt.Core.Monitoring;
using Yanmt.Core.Monitors;
using Yanmt.Core.Status;

namespace Yanmt.Core.Tests.Monitors;

public class ValueMapTests
{
    [Fact]
    public void Parses_number_label_pairs_in_any_separator()
    {
        var map = ValueMap.Parse("1=up, 2=down;3 = testing\n7=lowerLayerDown", out var error);

        Assert.Null(error);
        Assert.Equal("up", map.Label(1));
        Assert.Equal("lowerLayerDown", map.Label(7m));
        Assert.Null(map.Label(4));
        Assert.Null(map.Label(1.5m));
        Assert.Equal(2, map.NumberOf("DOWN"));
        Assert.Equal("1=up, 2=down, 3=testing, 7=lowerLayerDown", map.ToText());
    }

    [Theory]
    [InlineData("up=1")]
    [InlineData("1=")]
    [InlineData("1=up, 1=down")]
    public void Refuses_malformed_maps(string text)
    {
        ValueMap.Parse(text, out var error);
        Assert.NotNull(error);
    }

    [Fact]
    public void Blank_is_empty() => Assert.True(ValueMap.Parse("  ", out _).IsEmpty);
}

public class RuleEvaluatorTests
{
    private static readonly ValueMap BgpStates = ValueMap.Parse("1=idle,2=connect,3=active,4=opensent,5=openconfirm,6=established", out _);

    private static MonitorSpec Spec(params MonitorRule[] rules) => new() { Rules = rules, DefaultState = Severity.Warning, ValueMap = BgpStates };

    private static MonitorValue N(decimal n) => new(n, null);

    [Fact]
    public void First_matching_rule_wins_and_default_applies_otherwise()
    {
        var spec = Spec(
            new(RuleOperator.Equal, "6", null, Severity.Normal),
            new(RuleOperator.Less, "6", null, Severity.Critical),
            new(RuleOperator.Less, "3", null, Severity.Warning));

        Assert.Equal((Severity.Normal, 0), RuleEvaluator.Evaluate(spec, N(6)));
        Assert.Equal((Severity.Critical, 1), RuleEvaluator.Evaluate(spec, N(1))); // rule 3 also matches, but 2 is first
        Assert.Equal((Severity.Warning, (int?)null), RuleEvaluator.Evaluate(spec, N(9)));
    }

    [Theory]
    [InlineData(RuleOperator.Equal, "established", null, 6, true)]
    [InlineData(RuleOperator.Equal, "Established", null, 6, true)]
    [InlineData(RuleOperator.NotEqual, "established", null, 3, true)]
    [InlineData(RuleOperator.NotEqual, "6", null, 6, false)]
    [InlineData(RuleOperator.GreaterOrEqual, "opensent", null, 5, true)]
    [InlineData(RuleOperator.Between, "2", "4", 4, true)]
    [InlineData(RuleOperator.Between, "4", "2", 3, true)]
    [InlineData(RuleOperator.Between, "4", "5", 6, false)]
    [InlineData(RuleOperator.Contains, "establ", null, 6, true)]
    [InlineData(RuleOperator.Regex, "^(idle|active)$", null, 3, true)]
    [InlineData(RuleOperator.Regex, "^(idle|active)$", null, 6, false)]
    public void Operators_use_the_number_or_the_mapped_label(RuleOperator op, string v1, string? v2, int value, bool expected) =>
        Assert.Equal(expected, RuleEvaluator.Matches(op, v1, v2, N(value), BgpStates));

    [Fact]
    public void Text_values_compare_as_text()
    {
        var text = new MonitorValue(null, "Tunnel0 is up");

        Assert.True(RuleEvaluator.Matches(RuleOperator.Contains, "up", null, text, ValueMap.Empty));
        Assert.True(RuleEvaluator.Matches(RuleOperator.Equal, "tunnel0 is up", null, text, ValueMap.Empty));
        Assert.False(RuleEvaluator.Matches(RuleOperator.Greater, "1", null, text, ValueMap.Empty)); // not a number: no match
    }

    [Fact]
    public void A_catastrophic_regex_times_out_as_no_match() =>
        Assert.False(RuleEvaluator.Matches(RuleOperator.Regex, "^(a+)+$", null, new MonitorValue(null, new string('a', 5000) + "!"), ValueMap.Empty));

    [Fact]
    public void Row_filters_use_the_filter_column_and_label_regex()
    {
        var spec = new MonitorSpec
        {
            OidMode = OidMode.Table, FilterOid = "1.3.6.1.2.1.2.2.1.7", FilterOperator = RuleOperator.Equal, FilterValue = "1", LabelRegex = "^Gi",
        };

        Assert.True(RuleEvaluator.KeepRow(spec, new RawValue(1, null), "GigabitEthernet1"));
        Assert.False(RuleEvaluator.KeepRow(spec, new RawValue(2, null), "GigabitEthernet2")); // admin down
        Assert.False(RuleEvaluator.KeepRow(spec, new RawValue(1, null), "Null0"));
        Assert.False(RuleEvaluator.KeepRow(spec, RawValue.NoSuchObject, "GigabitEthernet3"));
        Assert.True(RuleEvaluator.KeepRow(new MonitorSpec { OidMode = OidMode.Table }, null, null));
    }
}

public class MonitorMathTests
{
    private static readonly DateTime T0 = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Raw_value_is_scaled()
    {
        var r = MonitorMath.Compute(new MonitorSpec { Scale = 2 }, new RawValue(21, null), null, T0, null, null);

        Assert.Equal(42m, r.Value!.Value.Number);
    }

    [Fact]
    public void Rate_needs_two_samples_and_prefers_agent_uptime_for_elapsed_time()
    {
        var spec = new MonitorSpec { ValueMode = ValueMode.Rate };
        var first = MonitorMath.Compute(spec, new RawValue(1000, null, 64), null, T0, 100_000, null);
        Assert.Null(first.Value);
        Assert.Equal("waiting for a second sample", first.Problem);

        // Wall clock says 70 s (poll jitter); the agent says 60 s.
        var second = MonitorMath.Compute(spec, new RawValue(7000, null, 64), null, T0.AddSeconds(70), 106_000, first.Baseline);
        Assert.Equal(100m, second.Value!.Value.Number);
    }

    [Fact]
    public void Rate_without_uptime_uses_the_wall_clock()
    {
        var spec = new MonitorSpec { ValueMode = ValueMode.Rate };
        var r = MonitorMath.Compute(spec, new RawValue(1600, null, 64), null, T0.AddSeconds(60), null, new RateBaseline(1000, T0, null));

        Assert.Equal(10m, r.Value!.Value.Number);
    }

    [Fact]
    public void Counter32_wrap_is_handled_and_counter64_reset_restarts()
    {
        var spec = new MonitorSpec { ValueMode = ValueMode.Rate };
        var wrapped = MonitorMath.Compute(spec, new RawValue(100, null, 32), null, T0.AddSeconds(10), null, new RateBaseline(4294967196m, T0, null));
        Assert.Equal(20m, wrapped.Value!.Value.Number);

        var reset = MonitorMath.Compute(spec, new RawValue(5, null, 64), null, T0.AddSeconds(10), null, new RateBaseline(1_000_000, T0, null));
        Assert.Null(reset.Value);
        Assert.StartsWith("counter reset", reset.Problem);
        Assert.Equal(5m, reset.Baseline!.Raw);
    }

    [Fact]
    public void Uptime_going_backwards_restarts_the_baseline()
    {
        var spec = new MonitorSpec { ValueMode = ValueMode.Rate };
        var r = MonitorMath.Compute(spec, new RawValue(50, null, 32), null, T0.AddSeconds(60), 3_000, new RateBaseline(9_000, T0, 900_000));

        Assert.Null(r.Value);
        Assert.StartsWith("device restarted", r.Problem);
    }

    [Fact]
    public void Interface_utilization_is_rate_times_8_over_speed_times_a_million()
    {
        // 12.5 MB/s on a 1000 Mb/s link = 10%.
        var spec = new MonitorSpec { ValueMode = ValueMode.Rate, Scale = 8, DenominatorOid = "1.3.6.1.2.1.31.1.1.1.15", DenominatorScale = 1_000_000, Unit = "%" };
        var r = MonitorMath.Compute(spec, new RawValue(125_000_000, null, 64), new RawValue(1000, null), T0.AddSeconds(10), null, new RateBaseline(0, T0, null));

        Assert.Equal(10m, r.Value!.Value.Number);
        Assert.Equal("10%", MonitorMath.Display(r.Value.Value, ValueMap.Empty, "%"));
    }

    [Fact]
    public void Percent_of_used_over_total()
    {
        var spec = new MonitorSpec { DenominatorOid = "1.3.6.1.4.1.9.9.48.1.1.1.6" };

        Assert.Equal(25m, MonitorMath.Compute(spec, new RawValue(250, null), new RawValue(1000, null), T0, null, null).Value!.Value.Number);
        Assert.Equal("denominator is 0", MonitorMath.Compute(spec, new RawValue(250, null), new RawValue(0, null), T0, null, null).Problem);
        Assert.Equal("denominator not available", MonitorMath.Compute(spec, new RawValue(250, null), RawValue.NoSuchObject, T0, null, null).Problem);
    }

    [Fact]
    public void Missing_object_has_no_value() =>
        Assert.Equal("no such object or instance", MonitorMath.Compute(new MonitorSpec(), RawValue.NoSuchObject, null, T0, null, null).Problem);

    [Fact]
    public void Display_shows_label_and_number_or_unit()
    {
        var map = ValueMap.Parse("1=up,2=down", out _);

        Assert.Equal("down (2)", MonitorMath.Display(new MonitorValue(2, null), map, null));
        Assert.Equal("1,234 errors/s", MonitorMath.Display(new MonitorValue(1234.4m, null), ValueMap.Empty, "errors/s"));
        Assert.Equal("0.25", MonitorMath.Display(new MonitorValue(0.2512m, null), ValueMap.Empty, null));
        Assert.Equal("Tunnel0", MonitorMath.Display(new MonitorValue(null, "Tunnel0"), ValueMap.Empty, null));
    }
}

public class MonitorDampeningTests
{
    private static readonly DampedState Normal = new(Severity.Normal, null, 0);

    [Fact]
    public void A_change_must_hold_for_the_required_polls()
    {
        var s = MonitorDampening.Apply(Normal, Severity.Critical, 3);
        Assert.Equal(new DampedState(Severity.Normal, Severity.Critical, 1), s);
        s = MonitorDampening.Apply(s, Severity.Critical, 3);
        Assert.Equal(Severity.Normal, s.State);
        s = MonitorDampening.Apply(s, Severity.Critical, 3);
        Assert.Equal(new DampedState(Severity.Critical, null, 0), s);
    }

    [Fact]
    public void Recovery_is_dampened_too_and_a_bounce_resets_the_count()
    {
        var critical = new DampedState(Severity.Critical, null, 0);
        var s = MonitorDampening.Apply(critical, Severity.Normal, 2);
        Assert.Equal(Severity.Critical, s.State);
        s = MonitorDampening.Apply(s, Severity.Critical, 2); // flapped back
        Assert.Equal(new DampedState(Severity.Critical, null, 0), s);
    }

    [Fact]
    public void First_data_is_taken_immediately() =>
        Assert.Equal(Severity.Critical, MonitorDampening.Apply(DampedState.Unknown, Severity.Critical, 5).State);
}

public class MonitorValidationTests
{
    private static MonitorSpec Valid => new()
    {
        Name = "Interface oper status", OidMode = OidMode.Table, Oid = "1.3.6.1.2.1.2.2.1.8", LabelOid = "1.3.6.1.2.1.2.2.1.2",
        Rules = [new(RuleOperator.Equal, "1", null, Severity.Normal)],
    };

    [Fact]
    public void A_sound_definition_passes() => Assert.Empty(MonitorValidation.Validate(Valid, null));

    [Fact]
    public void Names_and_oids_are_checked()
    {
        var errors = MonitorValidation.Validate(Valid with { Name = " ", Oid = "ifOperStatus" }, null);

        Assert.Contains(errors, e => e.Field == "Name");
        Assert.Contains(errors, e => e.Field == "Oid");
    }

    [Theory]
    [InlineData(RuleOperator.Greater, "abc", null)]
    [InlineData(RuleOperator.Between, "1", null)]
    [InlineData(RuleOperator.Regex, "([", null)]
    [InlineData(RuleOperator.Equal, "", null)]
    public void Bad_rules_are_refused(RuleOperator op, string v1, string? v2) =>
        Assert.Contains(MonitorValidation.Validate(Valid with { Rules = [new(op, v1, v2, Severity.Critical)] }, null), e => e.Error.StartsWith("Rule 1:"));

    [Fact]
    public void Numeric_rules_may_use_value_map_labels()
    {
        var spec = Valid with { ValueMap = ValueMap.Parse("1=up,2=down", out _), Rules = [new(RuleOperator.Greater, "up", null, Severity.Warning)] };

        Assert.Empty(MonitorValidation.Validate(spec, null));
    }

    [Fact]
    public void Row_filters_are_table_only_and_need_a_value()
    {
        Assert.NotEmpty(MonitorValidation.Validate(Valid with { OidMode = OidMode.Scalar, Oid = "1.3.6.1.2.1.1.3.0", LabelOid = null, LabelRegex = "^Gi" }, null));
        Assert.NotEmpty(MonitorValidation.Validate(Valid with { FilterOid = "1.3.6.1.2.1.2.2.1.7", FilterOperator = RuleOperator.Equal }, null));
        Assert.NotEmpty(MonitorValidation.Validate(Valid with { LabelRegex = "([" }, null));
    }

    [Fact]
    public void Limits()
    {
        Assert.NotEmpty(MonitorValidation.Validate(Valid with { IntervalSeconds = 10 }, null));
        Assert.NotEmpty(MonitorValidation.Validate(Valid with { ConsecutiveMatches = 0 }, null));
        Assert.NotEmpty(MonitorValidation.Validate(Valid with { Scale = 0 }, null));
        Assert.True(MonitorValidation.IsInstanceSuffix("192.0.2.1"));
        Assert.False(MonitorValidation.IsInstanceSuffix("3; x"));
    }
}
