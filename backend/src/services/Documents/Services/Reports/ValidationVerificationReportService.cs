using Custodian.Documents.Compliance;
using Custodian.Documents.Data;
using Custodian.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Documents.Services.Reports;

/// <summary>
/// CSTD-180: Implementation of the aggregate query engine for document validation and verification metrics.
/// Evaluates live document data within tenant isolation boundaries.
/// </summary>
public class ValidationVerificationReportService : IValidationVerificationReportService
{
    private readonly DocumentDbContext _dbContext;

    public ValidationVerificationReportService(DocumentDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ValidationVerificationData> ComputeAggregateAsync(
        string tenantId,
        ValidationVerificationFilter? filter = null,
        IReadOnlyCollection<Guid>? allowedEngagementIds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ArgumentException("TenantId is required.", nameof(tenantId));
        }

        var normalizedTenantId = tenantId.Trim();

        if (filter != null)
        {
            ValidationVerificationFilter.Validate(filter.From, filter.To);
        }

        // If a non-null collection of allowed engagement IDs is empty (e.g., staff member with no assigned engagements),
        // return an empty result immediately without querying the database.
        if (allowedEngagementIds != null && allowedEngagementIds.Count == 0)
        {
            return ValidationVerificationData.Empty(normalizedTenantId);
        }

        // If a specific engagement was requested in filter, verify against allowedEngagementIds when restricted
        if (filter?.EngagementId.HasValue == true)
        {
            var requestedEngagementId = filter.EngagementId.Value;
            if (allowedEngagementIds != null && !allowedEngagementIds.Contains(requestedEngagementId))
            {
                // Staff-style call: requested engagement outside allowed engagements -> zero-result dataset (do not leak)
                return ValidationVerificationData.Empty(normalizedTenantId);
            }
        }

        var query = _dbContext.Documents
            .AsNoTracking()
            .Where(d => d.TenantId == normalizedTenantId && !d.IsDeleted);

        // Engagement filtering: target engagement filter takes precedence/intersects with allowedEngagementIds
        if (filter?.EngagementId.HasValue == true)
        {
            query = query.Where(d => d.EngagementId == filter.EngagementId.Value);
        }
        else if (allowedEngagementIds != null)
        {
            query = query.Where(d => allowedEngagementIds.Contains(d.EngagementId));
        }

        // Date range filtering (UTC calendar-day inclusive semantics)
        if (filter != null)
        {
            if (filter.FromUtc.HasValue)
            {
                var fromUtc = filter.FromUtc.Value;
                query = query.Where(d => d.UploadedAt >= fromUtc);
            }

            if (filter.ToUtcExclusive.HasValue)
            {
                var toUtcExclusive = filter.ToUtcExclusive.Value;
                query = query.Where(d => d.UploadedAt < toUtcExclusive);
            }
        }

        var records = await query
            .Select(d => new
            {
                d.ComplianceStatus,
                d.VerificationStatus,
                d.RejectionReason,
                d.VerificationReason,
                d.Type
            })
            .ToListAsync(ct);

        var totalUploads = records.Count;

        // Automatic compliance counts (explicit matching ensures unexpected statuses are not misattributed)
        var compliantCount = records.Count(d => string.Equals(d.ComplianceStatus, ComplianceStatus.Compliant, StringComparison.OrdinalIgnoreCase));
        var rejectedComplianceCount = records.Count(d => string.Equals(d.ComplianceStatus, ComplianceStatus.Rejected, StringComparison.OrdinalIgnoreCase));
        var pendingComplianceCount = records.Count(d => string.Equals(d.ComplianceStatus, ComplianceStatus.Pending, StringComparison.OrdinalIgnoreCase));

        var automaticCompliance = new AutomaticComplianceCounts(
            Compliant: compliantCount,
            Rejected: rejectedComplianceCount,
            Pending: pendingComplianceCount);

        // Human verification counts: strictly evaluated ONLY for automatically compliant documents (ComplianceStatus == Compliant)
        var autoCompliantRecords = records
            .Where(d => string.Equals(d.ComplianceStatus, ComplianceStatus.Compliant, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var verifiedCount = autoCompliantRecords.Count(d => string.Equals(d.VerificationStatus, DocumentVerificationStatus.Verified, StringComparison.OrdinalIgnoreCase));
        var rejectedVerificationCount = autoCompliantRecords.Count(d => string.Equals(d.VerificationStatus, DocumentVerificationStatus.Rejected, StringComparison.OrdinalIgnoreCase));
        var unverifiedCount = autoCompliantRecords.Count(d => string.Equals(d.VerificationStatus, DocumentVerificationStatus.Unverified, StringComparison.OrdinalIgnoreCase));
        var pendingVerificationCount = autoCompliantRecords.Count(d => string.Equals(d.VerificationStatus, DocumentVerificationStatus.Pending, StringComparison.OrdinalIgnoreCase));

        var humanVerification = new HumanVerificationCounts(
            Verified: verifiedCount,
            Rejected: rejectedVerificationCount,
            Unverified: unverifiedCount,
            Pending: pendingVerificationCount);

        // Document-type breakdown: deterministic ordering by count descending, then type ascending
        var byDocumentType = records
            .Where(d => !string.IsNullOrWhiteSpace(d.Type))
            .GroupBy(d => d.Type.Trim())
            .Select(g => new DocumentTypeCount(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Type, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Automatic compliance rejection reasons: strictly where ComplianceStatus == Rejected and RejectionReason is non-empty
        var automaticRejectionReasons = records
            .Where(d => string.Equals(d.ComplianceStatus, ComplianceStatus.Rejected, StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrWhiteSpace(d.RejectionReason))
            .GroupBy(d => d.RejectionReason!.Trim())
            .Select(g => new RejectionReasonCount(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Reason, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Human verification rejection reasons: strictly where ComplianceStatus == Compliant, VerificationStatus == Rejected, and VerificationReason is non-empty
        var humanVerificationRejectionReasons = autoCompliantRecords
            .Where(d => string.Equals(d.VerificationStatus, DocumentVerificationStatus.Rejected, StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrWhiteSpace(d.VerificationReason))
            .GroupBy(d => d.VerificationReason!.Trim())
            .Select(g => new RejectionReasonCount(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Reason, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ValidationVerificationData(
            TenantId: normalizedTenantId,
            TotalUploads: totalUploads,
            AutomaticCompliance: automaticCompliance,
            HumanVerification: humanVerification,
            ByDocumentType: byDocumentType,
            AutomaticRejectionReasons: automaticRejectionReasons,
            HumanVerificationRejectionReasons: humanVerificationRejectionReasons);
    }
}
