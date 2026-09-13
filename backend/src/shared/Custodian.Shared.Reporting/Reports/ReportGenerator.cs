using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Custodian.Shared.Reporting.Reports;

public sealed class ReportGenerator
{
    public ReportOutput GenerateCsv<T>(
        ReportDefinition<T> definition,
        IEnumerable<T> rows)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(rows);

        var builder = new StringBuilder();
        AppendCsvRow(builder, definition.Columns.Select(column => column.Header));

        // process each data row
        foreach (var row in rows)
        {
            AppendCsvRow(builder, definition.Columns.Select(column =>
                Convert.ToString(column.Value(row), CultureInfo.InvariantCulture) ?? string.Empty));
        }

        return new ReportOutput(
            definition.Name,
            Encoding.UTF8.GetBytes(builder.ToString()),
            "text/csv; charset=utf-8");
    }

    public ReportOutput GenerateJson<T>(
        ReportDefinition<T> definition,
        IEnumerable<T> rows)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(rows);

        var projectedRows = rows.Select(row => definition.Columns.ToDictionary(
            column => column.Header,
            column => column.Value(row)));

        return new ReportOutput(
            definition.Name,
            JsonSerializer.SerializeToUtf8Bytes(projectedRows),
            "application/json; charset=utf-8");
    }

    private static void AppendCsvRow(StringBuilder builder, IEnumerable<string> values)
    {
        builder.AppendLine(string.Join(',', values.Select(EscapeCsvValue)));
    }

    private static string EscapeCsvValue(string value)
    {
        if (!value.Contains(',', StringComparison.Ordinal) &&
            !value.Contains('"', StringComparison.Ordinal) &&
            !value.Contains('\r', StringComparison.Ordinal) &&
            !value.Contains('\n', StringComparison.Ordinal))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}