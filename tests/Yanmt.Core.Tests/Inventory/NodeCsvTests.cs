using Yanmt.Core.Inventory;

namespace Yanmt.Core.Tests.Inventory;

public class NodeCsvTests
{
    private static readonly NodeCsvRecord Hub = new(
        "rtr-hub-01", "192.0.2.1", "HQ, Building 2", ["core", "dmvpn"], NodeIconType.Router, true, true, true, true);

    private static readonly NodeCsvRecord Spoke = new(
        "rtr-spoke-07", "2001:db8::7", null, [], NodeIconType.Router, false, true, false, false);

    [Fact]
    public void Export_then_parse_round_trips()
    {
        var parsed = NodeCsv.Parse(NodeCsv.Export([Spoke, Hub]));

        Assert.False(parsed.HasErrors);
        var records = parsed.Rows.Select(r => r.Record!).ToList();
        Assert.Equal(2, records.Count);
        AssertSame(Hub, records[0]); // export sorts by hostname
        AssertSame(Spoke, records[1]);
    }

    [Fact]
    public void Export_header_has_no_credential_columns()
    {
        var header = NodeCsv.Export([Hub]).Split("\r\n")[0];
        Assert.Equal(string.Join(",", NodeCsv.Columns), header);
        foreach (var forbidden in new[] { "password", "secret", "community", "auth", "priv", "credential", "key" })
        {
            Assert.DoesNotContain(forbidden, header, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Minimal_file_uses_defaults()
    {
        var parsed = NodeCsv.Parse("hostname,management_ip\r\nsw-access-03,198.51.100.3\r\n");
        var rec = Assert.Single(parsed.Rows).Record!;

        Assert.Equal(NodeIconType.Generic, rec.Icon);
        Assert.True(rec.Enabled);
        Assert.True(rec.IcmpEnabled);
        Assert.False(rec.SnmpEnabled);
        Assert.False(rec.SshEnabled);
        Assert.Empty(rec.Tags);
        Assert.Null(rec.Site);
    }

    [Fact]
    public void Header_is_case_insensitive_and_order_independent()
    {
        var parsed = NodeCsv.Parse("Management_IP,ICON,Hostname\r\n203.0.113.9,FIREWALL,fw-edge-01\r\n");
        var rec = Assert.Single(parsed.Rows).Record!;
        Assert.Equal("fw-edge-01", rec.Hostname);
        Assert.Equal(NodeIconType.Firewall, rec.Icon);
    }

    [Fact]
    public void Missing_required_column_is_a_file_error()
    {
        var parsed = NodeCsv.Parse("hostname,site\r\nrtr1,HQ\r\n");
        Assert.True(parsed.HasErrors);
        Assert.Contains(parsed.FileErrors, e => e.Contains("management_ip"));
        Assert.Empty(parsed.Rows);
    }

    [Fact]
    public void Unknown_column_is_a_file_error()
    {
        var parsed = NodeCsv.Parse("hostname,management_ip,snmp_community\r\nrtr1,192.0.2.1,public\r\n");
        Assert.Contains(parsed.FileErrors, e => e.Contains("snmp_community"));
    }

    [Fact]
    public void Row_errors_carry_line_numbers_and_all_messages()
    {
        var csv = "hostname,management_ip,icon,enabled\r\n"
                  + "rtr-ok,192.0.2.1,router,yes\r\n"
                  + "bad host,192.0.2.300,toaster,maybe\r\n";
        var parsed = NodeCsv.Parse(csv);

        Assert.True(parsed.Rows[0].IsValid);
        var bad = parsed.Rows[1];
        Assert.False(bad.IsValid);
        Assert.Equal(3, bad.Line);
        Assert.Equal(4, bad.Errors.Count); // hostname, IP, icon, enabled
    }

    [Fact]
    public void Numeric_icon_values_are_rejected()
    {
        var parsed = NodeCsv.Parse("hostname,management_ip,icon\r\nrtr1,192.0.2.1,2\r\n");
        Assert.False(parsed.Rows[0].IsValid);
    }

    [Fact]
    public void Duplicate_hostname_in_file_is_an_error_case_insensitively()
    {
        var parsed = NodeCsv.Parse("hostname,management_ip\r\nrtr1,192.0.2.1\r\nRTR1,192.0.2.2\r\n");
        Assert.True(parsed.Rows[0].IsValid);
        Assert.Contains(parsed.Rows[1].Errors, e => e.Contains("line 2"));
    }

    [Fact]
    public void Too_many_fields_is_an_error()
    {
        var parsed = NodeCsv.Parse("hostname,management_ip\r\nrtr1,192.0.2.1,extra\r\n");
        Assert.False(parsed.Rows[0].IsValid);
    }

    [Fact]
    public void Blank_rows_are_ignored_and_empty_file_is_an_error()
    {
        Assert.Single(NodeCsv.Parse("hostname,management_ip\r\n,\r\nrtr1,192.0.2.1\r\n").Rows);
        Assert.True(NodeCsv.Parse("").HasErrors);
    }

    [Fact]
    public void Ip_is_normalized_on_import()
    {
        var rec = NodeCsv.Parse("hostname,management_ip\r\nrtr1,2001:DB8:0:0::1\r\n").Rows[0].Record!;
        Assert.Equal("2001:db8::1", rec.ManagementIp);
    }

    private static void AssertSame(NodeCsvRecord expected, NodeCsvRecord actual)
    {
        Assert.Equal(expected with { Tags = [] }, actual with { Tags = [] });
        Assert.Equal(expected.Tags, actual.Tags);
    }
}
