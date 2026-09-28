using Custodian.Shared.Reporting.Models;

namespace Custodian.Shared.Reporting.Export;

/// <summary>CSTD-36-2: exports one report table (e.g. a per-action detail table) as a CSV file.</summary>
public interface ICsvExporter
{
    /// <summary>
    /// UTF-8 with BOM, RFC 4180 (comma separated, CRLF rows, quoted where needed), header row first.
    /// Values use <see cref="ReportValue.Format"/>: invariant numbers, ISO-8601 UTC dates.
    /// </summary>
    byte[] ToCsv(TableSection table);
}
