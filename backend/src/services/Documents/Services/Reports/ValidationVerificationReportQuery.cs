using System.Globalization;
using Custodian.Shared.Reporting.Errors;

namespace Custodian.Documents.Services.Reports;

/// <summary>
/// CSTD-182: Raw query parameters received on <c>GET /api/reports/validation-verification</c>.
/// </summary>
public sealed class ValidationVerificationReportQuery
{
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>Optional start date of document upload in <c>yyyy-MM-dd</c> format (UTC).</summary>
    public string? From { get; set; }

    /// <summary>Optional end date of document upload in <c>yyyy-MM-dd</c> format (UTC, inclusive).</summary>
    public string? To { get; set; }

    /// <summary>Optional engagement filter identifier (Guid).</summary>
    public string? EngagementId { get; set; }

    /// <summary>Report format: <c>pdf</c> (default) or <c>csv</c>.</summary>
    public string? Format { get; set; }

    /// <summary>
    /// Validates raw query parameters and converts them to a domain-level <see cref="ValidationVerificationFilter"/>.
    /// Throws <see cref="ReportGenerationException"/> with <see cref="ReportErrorKind.InvalidFilter"/> on any syntax or validation issue.
    /// </summary>
    public ValidationVerificationFilter ToFilter()
    {
        var fromDate = ParseDate(From, "from");
        var toDate = ParseDate(To, "to");

        if (fromDate.HasValue && toDate.HasValue && fromDate.Value > toDate.Value)
        {
            throw ReportGenerationException.InvalidFilter("from", "'from' must be on or before 'to'.");
        }

        var engagementGuid = ParseEngagementId(EngagementId);

        return new ValidationVerificationFilter(fromDate, toDate, engagementGuid);
    }

    private static DateOnly? ParseDate(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        if (DateOnly.TryParseExact(value.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        throw ReportGenerationException.InvalidFilter(fieldName, $"'{fieldName}' must be a date in YYYY-MM-DD format.");
    }

    private static Guid? ParseEngagementId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        if (Guid.TryParse(value.Trim(), out var id) && id != Guid.Empty)
        {
            return id;
        }

        throw ReportGenerationException.InvalidFilter("engagementId", "'engagementId' is not a valid engagement id.");
    }
}
