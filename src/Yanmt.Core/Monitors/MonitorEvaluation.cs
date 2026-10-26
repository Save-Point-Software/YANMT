using System.Globalization;
using System.Text.RegularExpressions;
using Yanmt.Core.Monitoring;
using Yanmt.Core.Status;

namespace Yanmt.Core.Monitors;

/// <summary>A polled value as SNMP returned it: the exact number for numeric types, the text otherwise.</summary>
public readonly record struct RawValue(decimal? Number, string? Text, int CounterBits = 0, bool Missing = false)
{
    public static RawValue NoSuchObject => new(null, null, 0, true);
}

/// <summary>A monitor's computed value: a number (raw, rate or percent) or, for non-numeric objects, text.</summary>
public readonly record struct MonitorValue(decimal? Number, string? Text);

/// <summary>The previous counter sample a rate is computed against.</summary>
public sealed record RateBaseline(decimal Raw, DateTime AtUtc, uint? UptimeTicks);

/// <summary>A computed value, the baseline to keep for next time, or why there is no value yet.</summary>
public sealed record ComputeResult(MonitorValue? Value, RateBaseline? Baseline, string? Problem);

public static class MonitorMath
{
    /// <summary>
    /// Applies the value mode and percentage. Rates use the agent's sysUpTime for elapsed time
    /// when it's available (immune to poll jitter); a sysUpTime that went backwards, or a
    /// counter that went down when it can't have wrapped, restarts the baseline instead of
    /// producing a bogus spike.
    /// </summary>
    public static ComputeResult Compute(MonitorSpec spec, RawValue primary, RawValue? denominator, DateTime nowUtc, uint? uptimeTicks, RateBaseline? previous)
    {
        if (primary.Missing)
        {
            return new(null, null, "no such object or instance");
        }

        decimal? number = primary.Number;
        RateBaseline? baseline = null;
        if (spec.ValueMode == ValueMode.Rate)
        {
            if (primary.Number is not { } raw)
            {
                return new(null, null, "a rate needs a numeric value");
            }

            baseline = new RateBaseline(raw, nowUtc, uptimeTicks);
            if (previous is null)
            {
                return new(null, baseline, "waiting for a second sample");
            }

            if (uptimeTicks is { } up && previous.UptimeTicks is { } prevUp && up < prevUp)
            {
                return new(null, baseline, "device restarted; waiting for a second sample");
            }

            var seconds = uptimeTicks is { } u2 && previous.UptimeTicks is { } p2 && u2 > p2
                ? (u2 - p2) / 100.0m
                : (decimal)(nowUtc - previous.AtUtc).TotalSeconds;
            if (seconds <= 0)
            {
                return new(null, baseline, "waiting for a second sample");
            }

            if (RateMath.Delta(previous.Raw, raw, primary.CounterBits == 32 ? 32 : 64) is not { } delta)
            {
                return new(null, baseline, "counter reset; waiting for a second sample");
            }

            number = delta / seconds;
        }

        if (number is null)
        {
            return spec.IsPercent
                ? new(null, baseline, "a percentage needs a numeric value")
                : new(new MonitorValue(null, primary.Text), baseline, null);
        }

        number *= spec.Scale;
        if (spec.IsPercent)
        {
            if (denominator is not { Number: { } d } || d * spec.DenominatorScale == 0)
            {
                return new(null, baseline, denominator is { Missing: true } or null ? "denominator not available" : "denominator is 0");
            }

            number = number / (d * spec.DenominatorScale) * 100;
        }

        return new(new MonitorValue(number, primary.Text), baseline, null);
    }

    /// <summary>"established (6)", "34.2 %", "1.2 Mb/s" style display text.</summary>
    public static string Display(MonitorValue value, ValueMap map, string? unit)
    {
        if (value.Number is not { } n)
        {
            return value.Text ?? "";
        }

        if (map.Label(n) is { } label)
        {
            return $"{label} ({Format(n)})";
        }

        return unit is { Length: > 0 } u ? (u == "%" ? Format(n) + "%" : $"{Format(n)} {u}") : Format(n);
    }

    public static string Format(decimal n)
    {
        var a = Math.Abs(n);
        var format = a >= 100 || a == decimal.Truncate(a) ? "#,0" : a >= 10 ? "0.#" : "0.##";
        return n.ToString(format, CultureInfo.InvariantCulture);
    }
}

/// <summary>State rules: first match wins, the default applies when none match, and no value means Unknown.</summary>
public static class RuleEvaluator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <returns>The state and the 0-based index of the matching rule (null when the default applied).</returns>
    public static (Severity State, int? Rule) Evaluate(MonitorSpec spec, MonitorValue value)
    {
        for (var i = 0; i < spec.Rules.Count; i++)
        {
            if (Matches(spec.Rules[i].Operator, spec.Rules[i].Value, spec.Rules[i].Value2, value, spec.ValueMap))
            {
                return (spec.Rules[i].State, i);
            }
        }

        return (spec.DefaultState, null);
    }

    /// <summary>
    /// Numeric operators compare the number. = and != compare numbers when the rule value is a
    /// number, otherwise the mapped label (or raw text). Contains and regex test the mapped label,
    /// or the raw text when there's no label.
    /// </summary>
    public static bool Matches(RuleOperator op, string ruleValue, string? ruleValue2, MonitorValue value, ValueMap map)
    {
        var text = value.Number is { } n ? map.Label(n) ?? value.Text ?? MonitorMath.Format(n) : value.Text ?? "";
        decimal? Number(string s) => decimal.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : map.NumberOf(s);

        switch (op)
        {
            case RuleOperator.Equal or RuleOperator.NotEqual:
            {
                bool equal;
                if (decimal.TryParse(ruleValue.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && value.Number is { } v)
                {
                    equal = v == r;
                }
                else if (value.Number is { } v2 && map.NumberOf(ruleValue) is { } mapped)
                {
                    equal = v2 == mapped;
                }
                else
                {
                    equal = string.Equals(text.Trim(), ruleValue.Trim(), StringComparison.OrdinalIgnoreCase);
                }

                return op == RuleOperator.Equal ? equal : !equal;
            }

            case RuleOperator.Contains:
                return ruleValue.Length > 0 && text.Contains(ruleValue, StringComparison.OrdinalIgnoreCase);

            case RuleOperator.Regex:
                try
                {
                    return Regex.IsMatch(text, ruleValue, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
                }
                catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentException)
                {
                    return false;
                }
        }

        if (value.Number is not { } x || Number(ruleValue) is not { } a)
        {
            return false;
        }

        return op switch
        {
            RuleOperator.Greater => x > a,
            RuleOperator.GreaterOrEqual => x >= a,
            RuleOperator.Less => x < a,
            RuleOperator.LessOrEqual => x <= a,
            RuleOperator.Between => ruleValue2 is not null && Number(ruleValue2) is { } b && x >= Math.Min(a, b) && x <= Math.Max(a, b),
            _ => false,
        };
    }

    /// <summary>Whether an all-rows table row is kept: its filter column passes the filter test and its label matches the regex.</summary>
    public static bool KeepRow(MonitorSpec spec, RawValue? filterValue, string? label)
    {
        if (spec.FilterOid is not null && spec.FilterOperator is { } op)
        {
            if (filterValue is not { Missing: false } f
                || !Matches(op, spec.FilterValue ?? "", null, new MonitorValue(f.Number, f.Text), ValueMap.Empty))
            {
                return false;
            }
        }

        if (spec.LabelRegex is { Length: > 0 } pattern)
        {
            try
            {
                return label is not null && Regex.IsMatch(label, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentException)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Consecutive-match dampening for custom monitors: every state change must hold for N polls.</summary>
public static class MonitorDampening
{
    public static DampedState Apply(DampedState current, Severity candidate, int required)
    {
        if (candidate == current.State)
        {
            return current with { PendingState = null, PendingCount = 0 };
        }

        // The first data a monitor gets is taken as-is: there's no state to protect yet.
        if (current.State == Severity.Unknown && current.PendingState is null && required > 1)
        {
            return new DampedState(candidate, null, 0);
        }

        var count = current.PendingState == candidate ? current.PendingCount + 1 : 1;
        return count >= Math.Max(1, required)
            ? new DampedState(candidate, null, 0)
            : current with { PendingState = candidate, PendingCount = count };
    }
}
