namespace Custodian.Shared.Reporting.Models;

/// <summary>
/// CSTD-36-1: a report as pure data: metadata plus ordered sections. Each report derives its own
/// model (e.g. the SLA performance report) and fills it from live queries in its owning service;
/// the renderer and CSV exporter only lay it out. Templates hold labels, never numbers or business text.
/// </summary>
public abstract class ReportModel
{
    protected ReportModel(ReportMetadata metadata, IEnumerable<ReportSection> sections)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(sections);

        var list = sections.ToArray();
        if (list.Length == 0)
        {
            // No data is still a report: the builder adds an EmptySection instead of no sections.
            throw new ArgumentException("A report needs at least one section; use EmptySection when nothing matches.", nameof(sections));
        }
        if (list.Any(s => s is null))
        {
            throw new ArgumentException("Report sections cannot be null.", nameof(sections));
        }

        Metadata = metadata;
        Sections = list;
    }

    public ReportMetadata Metadata { get; }

    public IReadOnlyList<ReportSection> Sections { get; }
}
