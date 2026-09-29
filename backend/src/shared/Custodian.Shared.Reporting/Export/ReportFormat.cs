using Custodian.Shared.Reporting.Errors;

namespace Custodian.Shared.Reporting.Export;

public enum ReportFormat
{
    Pdf,
    Csv
}

/// <summary>CSTD-36-3: the <c>format</c> query parameter of every report endpoint.</summary>
public static class ReportFormats
{
    public const string QueryParameter = "format";

    /// <summary>Missing or blank means PDF; anything but pdf/csv is a 400 invalid filter.</summary>
    public static ReportFormat Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ReportFormat.Pdf;

        return value.Trim().ToLowerInvariant() switch
        {
            "pdf" => ReportFormat.Pdf,
            "csv" => ReportFormat.Csv,
            _ => throw ReportGenerationException.InvalidFilter(QueryParameter, "Format must be 'pdf' or 'csv'.")
        };
    }

    public static string Extension(ReportFormat format) => format == ReportFormat.Csv ? "csv" : "pdf";

    public static string ContentType(ReportFormat format) =>
        format == ReportFormat.Csv ? "text/csv; charset=utf-8" : "application/pdf";
}
