using Yanmt.Core.Csv;

namespace Yanmt.Core.Status;

/// <summary>An event as it appears in CSV. Fields are display values only; there are no secrets.</summary>
public sealed record EventCsvRecord(
    long Id, DateTime TimestampUtc, string? Hostname, string Source,
    Severity? OldState, Severity NewState, string Message, string? AcknowledgedBy, DateTime? AcknowledgedUtc);

/// <summary>Events CSV export. Timestamps are ISO 8601 UTC; text goes through <see cref="CsvCodec"/> (formula-guarded).</summary>
public static class EventCsv
{
    public static readonly IReadOnlyList<string> Columns =
        ["id", "timestamp_utc", "node", "source", "old_state", "new_state", "message", "acknowledged_by", "acknowledged_utc"];

    /// <summary>Rows are written in the order given.</summary>
    public static string Export(IEnumerable<EventCsvRecord> events)
    {
        var rows = new List<IReadOnlyList<string?>> { Columns.ToArray() };
        rows.AddRange(events.Select(e => (IReadOnlyList<string?>)
        [
            e.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), Iso(e.TimestampUtc), e.Hostname, e.Source,
            e.OldState?.ToString(), e.NewState.ToString(), e.Message, e.AcknowledgedBy,
            e.AcknowledgedUtc is { } a ? Iso(a) : null,
        ]));
        return CsvCodec.Write(rows);
    }

    private static string Iso(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
