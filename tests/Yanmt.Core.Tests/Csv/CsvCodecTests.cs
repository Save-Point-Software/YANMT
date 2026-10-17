using Yanmt.Core.Csv;

namespace Yanmt.Core.Tests.Csv;

public class CsvCodecTests
{
    [Fact]
    public void Parses_simple_rows_with_crlf_and_lf()
    {
        var rows = CsvCodec.Parse("a,b,c\r\n1,2,3\n4,5,6");
        Assert.Equal(3, rows.Count);
        Assert.Equal(["4", "5", "6"], rows[2]);
    }

    [Fact]
    public void Parses_quoted_fields_with_commas_quotes_and_newlines()
    {
        var rows = CsvCodec.Parse("name,notes\r\n\"rtr, hub\",\"says \"\"hi\"\"\r\nline 2\"\r\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal("rtr, hub", rows[1][0]);
        Assert.Equal("says \"hi\"\r\nline 2", rows[1][1]);
    }

    [Fact]
    public void Keeps_empty_fields()
    {
        var rows = CsvCodec.Parse("a,,c,\r\n");
        Assert.Equal(["a", "", "c", ""], rows[0]);
    }

    [Fact]
    public void Skips_blank_lines_and_strips_bom()
    {
        var rows = CsvCodec.Parse("﻿h1,h2\r\n\r\nx,y\r\n\r\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal("h1", rows[0][0]);
    }

    [Fact]
    public void Unterminated_quote_is_an_error()
    {
        Assert.Throws<FormatException>(() => CsvCodec.Parse("a,\"b\r\n"));
    }

    [Theory]
    [InlineData("=cmd|' /C calc'!A0")]
    [InlineData("+1+1")]
    [InlineData("-2")]
    [InlineData("@SUM(A1)")]
    public void Escape_neutralizes_formulas_and_import_reverses_it(string value)
    {
        var escaped = CsvCodec.Escape(value);
        Assert.StartsWith("'", escaped.TrimStart('"'));
        var parsed = CsvCodec.Parse(escaped)[0][0];
        Assert.Equal(value, CsvCodec.UnescapeFormulaGuard(parsed));
    }

    [Fact]
    public void Write_then_parse_round_trips()
    {
        string?[][] rows =
        [
            ["hostname", "site", "tags"],
            ["rtr-hub-01", "HQ, Building 2", "core;dmvpn"],
            ["sw\"quoted\"", null, " padded "],
        ];

        var parsed = CsvCodec.Parse(CsvCodec.Write(rows));
        Assert.Equal(3, parsed.Count);
        Assert.Equal("HQ, Building 2", parsed[1][1]);
        Assert.Equal("sw\"quoted\"", parsed[2][0]);
        Assert.Equal("", parsed[2][1]);
        Assert.Equal(" padded ", parsed[2][2]);
    }
}
