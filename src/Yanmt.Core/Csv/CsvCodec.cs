using System.Text;

namespace Yanmt.Core.Csv;

/// <summary>
/// Minimal RFC 4180 reader/writer: quoted fields, embedded commas/quotes/newlines, CRLF or LF.
/// Hand-rolled to avoid a package; YANMT only needs it for node import/export and event export.
/// </summary>
public static class CsvCodec
{
    private static readonly char[] FormulaPrefixes = ['=', '+', '-', '@', '\t', '\r'];

    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;

        // Strip a UTF-8 BOM if the file came from Excel.
        var i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"' when field.Length == 0:
                    inQuotes = true;
                    fieldStarted = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    fieldStarted = true;
                    break;
                case '\r':
                    break; // handled by '\n'; a bare CR is dropped
                case '\n':
                    EndRow();
                    break;
                default:
                    field.Append(c);
                    fieldStarted = true;
                    break;
            }
        }

        if (inQuotes)
        {
            throw new FormatException("CSV ends inside a quoted field (missing closing quote).");
        }

        EndRow();
        return rows;

        void EndRow()
        {
            if (fieldStarted || field.Length > 0 || fields.Count > 0)
            {
                fields.Add(field.ToString());
                rows.Add([.. fields]);
            }

            fields.Clear();
            field.Clear();
            fieldStarted = false;
        }
    }

    public static string Write(IEnumerable<IReadOnlyList<string?>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append(Escape(row[i]));
            }

            sb.Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Quotes when needed and neutralizes spreadsheet formula injection: a cell starting with
    /// = + - @ (or tab/CR) gets a leading apostrophe so Excel treats it as text.
    /// <see cref="UnescapeFormulaGuard"/> reverses this on import.
    /// </summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (FormulaPrefixes.Contains(value[0]))
        {
            value = "'" + value;
        }

        var needsQuotes = value.AsSpan().IndexOfAny(",\"\r\n") >= 0 || value[0] == ' ' || value[^1] == ' ';
        return needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    /// <summary>Strips the apostrophe that <see cref="Escape"/> adds in front of formula-like values.</summary>
    public static string UnescapeFormulaGuard(string value) =>
        value.Length >= 2 && value[0] == '\'' && FormulaPrefixes.Contains(value[1]) ? value[1..] : value;
}
