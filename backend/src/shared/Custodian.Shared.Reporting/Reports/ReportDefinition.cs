namespace Custodian.Shared.Reporting.Reports;

public sealed class ReportDefinition<T>
{
    public ReportDefinition(
        string name,
        IEnumerable<ReportColumn<T>> columns)
    {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A report name is required.", nameof(name))
            : name;

        Columns = columns?.ToArray()
            ?? throw new ArgumentNullException(nameof(columns));

        if (Columns.Count == 0)
        {
            throw new ArgumentException("At least one report column is required.", nameof(columns));
        }

        if (Columns.Any(column => string.IsNullOrWhiteSpace(column.Header)))
        {
            throw new ArgumentException("Every report column must have a header.", nameof(columns));
        }
    }

    public string Name { get; }

    public IReadOnlyList<ReportColumn<T>> Columns { get; }
}