namespace Custodian.Documents.Services.Reports;

/// <summary>
/// CSTD-181: Filter criteria for the Validation &amp; Verification Report aggregate query.
/// Supports optional UTC calendar-day date ranges (From/To) and optional EngagementId restriction.
/// </summary>
public sealed record ValidationVerificationFilter
{
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public Guid? EngagementId { get; init; }

    public ValidationVerificationFilter()
    {
    }

    public ValidationVerificationFilter(
        DateOnly? from = null,
        DateOnly? to = null,
        Guid? engagementId = null)
    {
        Validate(from, to);
        From = from;
        To = to;
        EngagementId = engagementId;
    }

    /// <summary>
    /// Inclusive lower UTC boundary: UploadedAt &gt;= FromUtc (00:00:00 UTC).
    /// </summary>
    public DateTime? FromUtc => From?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    /// <summary>
    /// Exclusive upper UTC boundary: UploadedAt &lt; ToUtcExclusive (the day after To at 00:00:00 UTC).
    /// Covers the entire inclusive calendar day of To.
    /// </summary>
    public DateTime? ToUtcExclusive => To?.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    /// <summary>
    /// Validates that From date is on or before To date when both are specified.
    /// </summary>
    public static void Validate(DateOnly? from, DateOnly? to)
    {
        if (from.HasValue && to.HasValue && from.Value > to.Value)
        {
            throw new ArgumentException(
                $"Invalid date range: 'From' date ({from.Value:yyyy-MM-dd}) cannot be after 'To' date ({to.Value:yyyy-MM-dd}).",
                nameof(from));
        }
    }
}
