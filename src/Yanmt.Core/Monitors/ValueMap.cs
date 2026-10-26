using System.Globalization;
using System.Text;

namespace Yanmt.Core.Monitors;

/// <summary>Integer-to-label map, e.g. "1=up, 2=down" for ifOperStatus. Written and read as text.</summary>
public sealed class ValueMap
{
    public const int MaxEntries = 64;
    public const int MaxLabelLength = 40;

    public static ValueMap Empty { get; } = new(new Dictionary<long, string>());

    private readonly IReadOnlyDictionary<long, string> _labels;

    private ValueMap(IReadOnlyDictionary<long, string> labels) => _labels = labels;

    public IReadOnlyDictionary<long, string> Labels => _labels;

    public bool IsEmpty => _labels.Count == 0;

    public string? Label(decimal? value) =>
        value is { } v && v == decimal.Truncate(v) && v is >= long.MinValue and <= long.MaxValue && _labels.TryGetValue((long)v, out var label)
            ? label
            : null;

    /// <summary>The number a label stands for (case-insensitive), for rules written as "= established".</summary>
    public long? NumberOf(string label)
    {
        foreach (var (n, l) in _labels)
        {
            if (string.Equals(l, label.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return n;
            }
        }

        return null;
    }

    /// <summary>Parses "1=up, 2=down" (commas, semicolons or new lines between entries). Blank is an empty map.</summary>
    public static ValueMap Parse(string? text, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return Empty;
        }

        var labels = new Dictionary<long, string>();
        foreach (var raw in text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = raw.IndexOf('=');
            if (eq <= 0 || !long.TryParse(raw[..eq].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                error = $"'{raw}' isn't a 'number=label' entry.";
                return Empty;
            }

            var label = raw[(eq + 1)..].Trim();
            if (label.Length is 0 or > MaxLabelLength || label.Any(char.IsControl))
            {
                error = $"The label for {number} must be 1 to {MaxLabelLength} characters.";
                return Empty;
            }

            if (!labels.TryAdd(number, label))
            {
                error = $"{number} is mapped twice.";
                return Empty;
            }
        }

        if (labels.Count > MaxEntries)
        {
            error = $"A value map can have at most {MaxEntries} entries.";
            return Empty;
        }

        return new ValueMap(labels);
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var (n, l) in _labels.OrderBy(kv => kv.Key))
        {
            if (sb.Length > 0)
            {
                sb.Append(", ");
            }

            sb.Append(n.ToString(CultureInfo.InvariantCulture)).Append('=').Append(l);
        }

        return sb.ToString();
    }
}
