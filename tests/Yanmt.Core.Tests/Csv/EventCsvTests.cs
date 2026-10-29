using Yanmt.Core.Csv;
using Yanmt.Core.Status;

namespace Yanmt.Core.Tests.Csv;

public class EventCsvTests
{
    [Fact]
    public void Export_writes_header_and_rows()
    {
        var csv = EventCsv.Export([
            new EventCsvRecord(7, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), "rtr-a", "Icmp",
                Severity.Normal, Severity.Critical, "Ping failed, 3 lost", "alice", new DateTime(2026, 1, 2, 4, 0, 0, DateTimeKind.Utc)),
            new EventCsvRecord(8, new DateTime(2026, 1, 2, 3, 5, 0, DateTimeKind.Utc), null, "ConfigChange",
                null, Severity.Warning, "=HYPERLINK(\"x\")", null, null),
        ]);

        var rows = CsvCodec.Parse(csv);
        Assert.Equal(EventCsv.Columns, rows[0]);
        Assert.Equal(["7", "2026-01-02T03:04:05Z", "rtr-a", "Icmp", "Normal", "Critical", "Ping failed, 3 lost", "alice", "2026-01-02T04:00:00Z"], rows[1]);
        Assert.Equal("", rows[2][2]);
        Assert.Equal("", rows[2][4]);
        Assert.Equal("'=HYPERLINK(\"x\")", rows[2][6]); // formula guard
        Assert.Equal("", rows[2][8]);
    }

    [Fact]
    public void Export_of_nothing_is_header_only()
        => Assert.Single(CsvCodec.Parse(EventCsv.Export([])));
}
