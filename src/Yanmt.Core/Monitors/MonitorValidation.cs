using System.Globalization;
using System.Text.RegularExpressions;

namespace Yanmt.Core.Monitors;

/// <summary>Validation for monitor definitions and assignments, shared by the editor and the service.</summary>
public static partial class MonitorValidation
{
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 1000;
    public const int MaxUnitLength = 16;
    public const int MaxRules = 32;
    public const int MaxRuleValueLength = 200;
    public const int MaxRegexLength = 200;
    public const int MaxOidLength = 256;
    public const int MinIntervalSeconds = 30;
    public const int MaxIntervalSeconds = 86400;
    public const int MaxConsecutiveMatches = 20;

    public static bool IsNumericOid(string? oid) => oid is { Length: > 0 and <= MaxOidLength } && NumericOid().IsMatch(oid);

    /// <summary>An instance suffix such as "3" or "192.0.2.1" (numeric sub-identifiers only).</summary>
    public static bool IsInstanceSuffix(string? suffix) => suffix is { Length: > 0 and <= MaxOidLength } && NumericOid().IsMatch(suffix);

    public static IReadOnlyList<(string Field, string Error)> Validate(MonitorSpec spec, string? description)
    {
        var errors = new List<(string, string)>();
        void Add(string field, string error) => errors.Add((field, error));

        if (string.IsNullOrWhiteSpace(spec.Name) || spec.Name.Trim().Length > MaxNameLength || spec.Name.Any(char.IsControl))
        {
            Add(nameof(spec.Name), $"Name is required (at most {MaxNameLength} characters).");
        }

        if (description?.Length > MaxDescriptionLength)
        {
            Add("Description", $"Description must be at most {MaxDescriptionLength} characters.");
        }

        if (!IsNumericOid(spec.Oid))
        {
            Add(nameof(spec.Oid), "OID must be numeric, e.g. 1.3.6.1.2.1.2.2.1.8 (names are converted when you type them).");
        }

        foreach (var (oid, field) in new[] { (spec.LabelOid, nameof(spec.LabelOid)), (spec.DenominatorOid, nameof(spec.DenominatorOid)), (spec.FilterOid, nameof(spec.FilterOid)) })
        {
            if (oid is not null && !IsNumericOid(oid))
            {
                Add(field, "Must be a numeric OID.");
            }
        }

        if (spec.OidMode == OidMode.Scalar && (spec.LabelOid is not null || spec.HasRowFilter))
        {
            Add(nameof(spec.LabelOid), "Row labels and row filters only apply to table columns.");
        }

        if (spec.Scale <= 0 || spec.Scale > 1_000_000_000_000m || spec.DenominatorScale <= 0 || spec.DenominatorScale > 1_000_000_000_000m)
        {
            Add(nameof(spec.Scale), "Scale factors must be positive.");
        }

        if (spec.Unit?.Length > MaxUnitLength)
        {
            Add(nameof(spec.Unit), $"Unit must be at most {MaxUnitLength} characters.");
        }

        if (spec.ConsecutiveMatches is < 1 or > MaxConsecutiveMatches)
        {
            Add(nameof(spec.ConsecutiveMatches), $"Consecutive matches must be between 1 and {MaxConsecutiveMatches}.");
        }

        if (spec.IntervalSeconds is < MinIntervalSeconds or > MaxIntervalSeconds)
        {
            Add(nameof(spec.IntervalSeconds), $"Interval must be between {MinIntervalSeconds} and {MaxIntervalSeconds} seconds (or blank for the SNMP default).");
        }

        if (spec.FilterOid is not null && (spec.FilterOperator is null || string.IsNullOrWhiteSpace(spec.FilterValue)))
        {
            Add(nameof(spec.FilterOid), "A row filter needs an operator and a value.");
        }

        if (spec.FilterOid is not null && spec.FilterOperator is { } fop && RuleError(fop, spec.FilterValue ?? "", null, ValueMap.Empty) is { } ferr)
        {
            Add(nameof(spec.FilterOid), "Row filter: " + ferr);
        }

        if (spec.LabelRegex is { } lr && (lr.Length > MaxRegexLength || !CompilesAsRegex(lr)))
        {
            Add(nameof(spec.LabelRegex), "Row label filter isn't a valid regular expression.");
        }

        if (spec.Rules.Count > MaxRules)
        {
            Add(nameof(spec.Rules), $"A monitor can have at most {MaxRules} rules.");
        }

        for (var i = 0; i < spec.Rules.Count; i++)
        {
            var r = spec.Rules[i];
            if (RuleError(r.Operator, r.Value, r.Value2, spec.ValueMap) is { } error)
            {
                Add(nameof(spec.Rules), $"Rule {i + 1}: {error}");
            }
        }

        return errors;
    }

    /// <summary>Why a rule (or filter) can't be evaluated, or null if it's fine.</summary>
    public static string? RuleError(RuleOperator op, string value, string? value2, ValueMap map)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxRuleValueLength || value.Any(char.IsControl))
        {
            return "a value is required.";
        }

        bool Numeric(string v) => decimal.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _) || map.NumberOf(v) is not null;

        return op switch
        {
            RuleOperator.Greater or RuleOperator.GreaterOrEqual or RuleOperator.Less or RuleOperator.LessOrEqual when !Numeric(value) =>
                $"'{value}' isn't a number (or a label from the value map).",
            RuleOperator.Between when string.IsNullOrWhiteSpace(value2) => "'between' needs two values.",
            RuleOperator.Between when !Numeric(value) || !Numeric(value2!) => "'between' needs two numbers.",
            RuleOperator.Regex when value.Length > MaxRegexLength || !CompilesAsRegex(value) => $"'{value}' isn't a valid regular expression.",
            _ => null,
        };
    }

    private static bool CompilesAsRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static string Symbol(RuleOperator op) => op switch
    {
        RuleOperator.Equal => "=",
        RuleOperator.NotEqual => "≠",
        RuleOperator.Greater => ">",
        RuleOperator.GreaterOrEqual => "≥",
        RuleOperator.Less => "<",
        RuleOperator.LessOrEqual => "≤",
        RuleOperator.Between => "between",
        RuleOperator.Contains => "contains",
        _ => "matches regex",
    };

    [GeneratedRegex(@"^\d+(\.\d+)*$")]
    private static partial Regex NumericOid();
}
