namespace Custodian.Documents.Services.Reports;

/// <summary>
/// CSTD-180 / CSTD-181: Service contract for computing the live Validation &amp; Verification Report aggregate dataset.
/// </summary>
public interface IValidationVerificationReportService
{
    /// <summary>
    /// Computes aggregate document validation and verification counts for a tenant workspace,
    /// optionally filtered by UTC calendar date range and engagement.
    /// Soft-deleted documents (IsDeleted == true) are strictly excluded from all aggregates.
    /// </summary>
    /// <param name="tenantId">The workspace tenant identifier. Must not be null or whitespace.</param>
    /// <param name="filter">Optional date range and engagement filters.</param>
    /// <param name="allowedEngagementIds">
    /// Optional collection of allowed engagement IDs:
    /// - null: entire tenant scope, intended for Owner callers.
    /// - non-null collection: restricts aggregation strictly to documents belonging to these engagements.
    /// - empty collection: returns an empty zero-result dataset immediately.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Deterministic aggregate totals, pass/fail counts, type breakdowns, and rejection reason breakdowns.</returns>
    Task<ValidationVerificationData> ComputeAggregateAsync(
        string tenantId,
        ValidationVerificationFilter? filter = null,
        IReadOnlyCollection<Guid>? allowedEngagementIds = null,
        CancellationToken ct = default);
}
