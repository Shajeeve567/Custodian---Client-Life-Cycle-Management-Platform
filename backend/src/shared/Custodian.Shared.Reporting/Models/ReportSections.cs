namespace Custodian.Shared.Reporting.Models;

/// <summary>
/// CSTD-36-1: report building blocks. Pure data: how each type is drawn is up to the renderer (PDF)
/// or exporter (CSV). Collections are copied on construction, so a section cannot change after it is built.
/// </summary>
public abstract class ReportSection
{
    protected ReportSection(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Title = title;
    }

    public string Title { get; }
}

/// <summary>A labelled value, e.g. "Total actions" → 42. Values follow <see cref="ReportValue"/>'s rules.</summary>
public sealed record ReportKeyValue(string Label, object? Value);

/// <summary>Label/value pairs, e.g. a summary block.</summary>
public sealed class KeyValueSection : ReportSection
{
    public KeyValueSection(string title, IEnumerable<ReportKeyValue> pairs) : base(title)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var list = pairs.ToArray();
        foreach (var pair in list)
        {
            ArgumentNullException.ThrowIfNull(pair, nameof(pairs));
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Label, nameof(pairs));
            ReportValue.EnsureSupported(pair.Value, $"{title} / {pair.Label}");
        }
        Pairs = list;
    }

    public IReadOnlyList<ReportKeyValue> Pairs { get; }
}

public enum ReportColumnAlignment
{
    Left,
    Right
}

/// <summary>A table column. Numbers read best right-aligned.</summary>
public sealed record ReportColumn(string Header, ReportColumnAlignment Alignment = ReportColumnAlignment.Left);

/// <summary>
/// Rows of typed cell values (see <see cref="ReportValue"/>). Every row has exactly one cell per column.
/// CSV exports a table section as-is, so builders keep raw values here and let the output format them.
/// </summary>
public sealed class TableSection : ReportSection
{
    public TableSection(
        string title,
        IEnumerable<ReportColumn> columns,
        IEnumerable<IEnumerable<object?>> rows,
        string? footnote = null) : base(title)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);

        var columnList = columns.ToArray();
        if (columnList.Length == 0)
        {
            throw new ArgumentException("A table needs at least one column.", nameof(columns));
        }
        foreach (var column in columnList)
        {
            ArgumentNullException.ThrowIfNull(column, nameof(columns));
            ArgumentException.ThrowIfNullOrWhiteSpace(column.Header, nameof(columns));
        }

        var rowList = new List<IReadOnlyList<object?>>();
        foreach (var row in rows)
        {
            ArgumentNullException.ThrowIfNull(row, nameof(rows));
            var cells = row.ToArray();
            if (cells.Length != columnList.Length)
            {
                throw new ArgumentException(
                    $"Row {rowList.Count + 1} of '{title}' has {cells.Length} cells; the table has {columnList.Length} columns.",
                    nameof(rows));
            }
            for (var i = 0; i < cells.Length; i++)
            {
                ReportValue.EnsureSupported(cells[i], $"{title} / row {rowList.Count + 1} / {columnList[i].Header}");
            }
            rowList.Add(cells);
        }

        Columns = columnList;
        Rows = rowList.AsReadOnly();
        Footnote = string.IsNullOrWhiteSpace(footnote) ? null : footnote;
    }

    public IReadOnlyList<ReportColumn> Columns { get; }

    public IReadOnlyList<IReadOnlyList<object?>> Rows { get; }

    public string? Footnote { get; }
}

/// <summary>Explanatory paragraphs, e.g. the metric definitions of a report.</summary>
public sealed class TextSection : ReportSection
{
    public TextSection(string title, IEnumerable<string> paragraphs) : base(title)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);
        var list = paragraphs.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (list.Length == 0)
        {
            throw new ArgumentException("A text section needs at least one paragraph.", nameof(paragraphs));
        }
        Paragraphs = list;
    }

    public IReadOnlyList<string> Paragraphs { get; }
}

/// <summary>
/// Stands in for a section with no data. Empty data is a valid report, not an error (CSTD-36 AC5).
/// </summary>
public sealed class EmptySection : ReportSection
{
    public const string DefaultMessage = "No records match the selected filters.";

    public EmptySection(string title, string message = DefaultMessage) : base(title)
    {
        Message = string.IsNullOrWhiteSpace(message) ? DefaultMessage : message;
    }

    public string Message { get; }
}
