namespace Custodian.Audit.Services.Reports;

/// <summary>
/// CSTD-246 & CSTD-247: Service contract for Proof-of-Delivery Report data aggregation and live verification.
/// </summary>
public interface IProofOfDeliveryReportService
{
    /// <summary>
    /// Executes the aggregate history query for an engagement within the caller's tenant.
    /// 1. Queries Workflow for engagement metadata and caller access check (fails closed if unreachable).
    /// 2. If engagement is inaccessible or not found, throws ReportGenerationException.SubjectNotFound.
    /// 3. Queries Audit repository for events ordered by sequence number within the tenant.
    /// 4. Computes exact history boundary and returns the complete aggregate snapshot.
    /// </summary>
    /// <param name="tenantId">Effective tenant ID from authenticated caller token.</param>
    /// <param name="engagementId">Target engagement ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Complete aggregate history data including confirmed engagement summary, event timeline, and boundary.</returns>
    Task<ProofOfDeliveryAggregateData> GetAggregateHistoryAsync(
        Guid tenantId,
        Guid engagementId,
        CancellationToken ct = default);

    /// <summary>
    /// CSTD-247: Recomputes and verifies the SHA-256 hash chain over the exact event snapshot in memory.
    /// Validates genesis previous-hash, chain links, and constant-time SHA-256 hash match on each event.
    /// Identifies the verified history boundary and distinguishes empty chains from actual delivery evidence.
    /// </summary>
    /// <param name="aggregateData">The snapshot aggregate data to verify.</param>
    /// <param name="isConsistentWithLiveHead">Whether the snapshot matches the current live database tip.</param>
    /// <returns>Explicit verification result (Valid, Empty, Tampered, or Unavailable) with boundary details.</returns>
    ProofOfDeliveryVerificationResult VerifyHistory(
        ProofOfDeliveryAggregateData aggregateData,
        bool isConsistentWithLiveHead = true);

    /// <summary>
    /// CSTD-247: Orchestrates end-to-end report data retrieval with live cryptographic verification.
    /// Safely checks for concurrent appends against live database tip and produces verified report data.
    /// </summary>
    /// <param name="tenantId">Effective tenant ID from authenticated caller token.</param>
    /// <param name="engagementId">Target engagement ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Verified report data containing aggregate snapshot and live cryptographic verification outcome.</returns>
    Task<ProofOfDeliveryVerifiedReportData> GetVerifiedReportDataAsync(
        Guid tenantId,
        Guid engagementId,
        CancellationToken ct = default);

    /// <summary>
    /// CSTD-248: Generates the full ProofOfDeliveryReport model containing visual report sections (PDF)
    /// and detail table (CSV export) from live verified data.
    /// </summary>
    /// <param name="tenantId">Effective tenant ID from authenticated caller token.</param>
    /// <param name="engagementId">Target engagement ID.</param>
    /// <param name="generatedBy">Caller identity or username.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Complete ProofOfDeliveryReport ready for rendering.</returns>
    Task<ProofOfDeliveryReport> BuildReportAsync(
        Guid tenantId,
        Guid engagementId,
        string generatedBy,
        CancellationToken ct = default);
}
