using System.Text;
using Custodian.Shared.Reporting.Models;

namespace Custodian.Shared.Reporting.Export;

/// <summary>
/// CSTD-36-2: RFC 4180 CSV. The BOM makes Excel read the file as UTF-8 (otherwise non-ASCII names
/// are garbled). The table title and footnote are not written: the file holds just the data.
/// </summary>
public sealed class CsvExporter : ICsvExporter
{
    private const string LineEnding = "\r\n";

    // A text cell starting with one of these is run as a formula by Excel/Sheets ("CSV injection").
    // Report text includes user-entered values (task titles, file names), so those cells get a leading
    // apostrophe. Numbers are written as numbers, so a negative number is left alone.
    private static readonly char[] FormulaTriggers = ['=', '+', '-', '@', '\t', '\r'];

    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    public byte[] ToCsv(TableSection table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var csv = new StringBuilder();
        AppendRow(csv, table.Columns.Select(c => (object?)c.Header));
        foreach (var row in table.Rows)
        {
            AppendRow(csv, row);
        }

        var preamble = Utf8WithBom.GetPreamble();
        var body = Utf8WithBom.GetBytes(csv.ToString());
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    private static void AppendRow(StringBuilder csv, IEnumerable<object?> values)
    {
        var first = true;
        foreach (var value in values)
        {
            if (!first) csv.Append(',');
            csv.Append(Field(value));
            first = false;
        }
        csv.Append(LineEnding);
    }

    private static string Field(object? value)
    {
        var text = ReportValue.Format(value);
        if (value is string && text.Length > 0 && FormulaTriggers.Contains(text[0]))
        {
            text = "'" + text;
        }

        var needsQuotes = text.IndexOfAny([',', '"', '\r', '\n']) >= 0
                          || (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1])));
        return needsQuotes ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
    }
}
